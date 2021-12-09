﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DurableTask.Core;
using DurableTask.Core.Command;
using DurableTask.Core.History;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static DurableTask.Protobuf.TaskHubSidecarService;
using P = DurableTask.Protobuf;

namespace DurableTask.Grpc;

// TODO: Rather than making this a top-level class, users should use TaskHubWorker.CreateBuilder().UseGrpc(address) or something similar to opt-into gRPC.
/// <summary>
/// Task hub worker that connects to a sidecar process over gRPC to execute orchestrator and activity events.
/// </summary>
public class DurableTaskGrpcWorker : IHostedService, IAsyncDisposable
{
    static readonly Google.Protobuf.WellKnownTypes.Empty EmptyMessage = new();

    readonly IServiceProvider services;
    readonly IDataConverter dataConverter;
    readonly ILogger logger;
    readonly IConfiguration? configuration;
    readonly GrpcChannel sidecarGrpcChannel;
    readonly TaskHubSidecarServiceClient sidecarClient;
    readonly WorkerContext workerContext;

    readonly ImmutableDictionary<TaskName, Func<WorkerContext, TaskOrchestration>> orchestrators;
    readonly ImmutableDictionary<TaskName, Func<WorkerContext, TaskActivity>> activities;

    CancellationTokenSource? shutdownTcs;
    Task? listenLoop;

    DurableTaskGrpcWorker(Builder builder)
    {
        this.services = builder.services ?? SdkUtils.EmptyServiceProvider;
        this.dataConverter = builder.dataConverter ?? this.services.GetService<IDataConverter>() ?? SdkUtils.DefaultDataConverter;
        this.logger = SdkUtils.GetLogger(builder.loggerFactory ?? this.services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance);
        this.configuration = builder.configuration ?? this.services.GetService<IConfiguration>();

        this.workerContext = new WorkerContext(
            this.dataConverter,
            this.logger,
            this.services,
            new ConcurrentDictionary<string, TaskActivity>(StringComparer.OrdinalIgnoreCase));

        this.orchestrators = builder.taskProvider.orchestratorsBuilder.ToImmutable();
        this.activities = builder.taskProvider.activitiesBuilder.ToImmutable();

        string sidecarAddress = builder.address ?? SdkUtils.GetSidecarAddress(this.configuration);
        this.sidecarGrpcChannel = GrpcChannel.ForAddress(sidecarAddress);
        this.sidecarClient = new TaskHubSidecarServiceClient(this.sidecarGrpcChannel);
    }

    /// <summary>
    /// Establishes a gRPC connection to the sidecar and starts processing work-items in the background.
    /// </summary>
    /// <remarks>
    /// This method retries continuously to establish a connection to the sidecar. If a connection fails,
    /// a warning log message will be written and a new connection attempt will be made. This process
    /// continues until either a connection succeeds or the caller cancels the start operation.
    /// </remarks>
    /// <param name="startupCancelToken">
    /// A cancellation token that can be used to cancel the sidecar connection attempt if it takes too long.
    /// </param>
    /// <returns>
    /// Returns a task that completes when the sidecar connection has been established and the background processing started.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if this worker is already started.</exception>
    public async Task StartAsync(CancellationToken startupCancelToken)
    {
        if (this.listenLoop?.IsCompleted == false)
        {
            throw new InvalidOperationException($"This {nameof(DurableTaskGrpcWorker)} is already started.");
        }

        this.logger.StartingTaskHubWorker(this.sidecarGrpcChannel.Target);

        // Keep trying to connect until the caller cancels
        while (true)
        {
            try
            {
                AsyncServerStreamingCall<P.WorkItem>? workItemStream = await this.ConnectAsync(startupCancelToken);

                this.shutdownTcs?.Dispose();
                this.shutdownTcs = new CancellationTokenSource();

                this.listenLoop = Task.Run(
                    () => this.WorkItemListenLoop(workItemStream, this.shutdownTcs.Token),
                    CancellationToken.None);
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                this.logger.SidecarUnavailable(this.sidecarGrpcChannel.Target);

                await Task.Delay(TimeSpan.FromSeconds(5), startupCancelToken);
            }
        }
    }

    /// <inheritdoc cref="StartAsync(CancellationToken)" />
    /// <param name="timeout">The maximum time to wait for a connection to be established.</param>
    public async Task StartAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        await this.StartAsync(cts.Token);
    }

    async Task<AsyncServerStreamingCall<P.WorkItem>> ConnectAsync(CancellationToken cancellationToken)
    {
        // Ping the sidecar to make sure it's up and listening.
        await this.sidecarClient.HelloAsync(EmptyMessage, cancellationToken: cancellationToken);
        this.logger.EstablishedWorkItemConnection();

        // Get the stream for receiving work-items
        return this.sidecarClient.GetWorkItems(new P.GetWorkItemsRequest(), cancellationToken: cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Cancelling the shutdownTcs causes the background processing to shutdown gracefully.
        this.shutdownTcs?.Cancel();

        // Wait for the listen loop to copmlete
        await (this.listenLoop?.WaitAsync(cancellationToken) ?? Task.CompletedTask);

        // TODO: Wait for any outstanding tasks to complete
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        // Shutdown with a default timeout of 30 seconds
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        try
        {
            await this.StopAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        GC.SuppressFinalize(this);
    }

    async Task WorkItemListenLoop(AsyncServerStreamingCall<P.WorkItem> workItemStream, CancellationToken shutdownToken)
    {
        bool reconnect = false;

        while (true)
        {
            try
            {
                if (reconnect)
                {
                    workItemStream = await this.ConnectAsync(shutdownToken);
                }

                await foreach (P.WorkItem workItem in workItemStream.ResponseStream.ReadAllAsync(shutdownToken))
                {
                    if (workItem.RequestCase == P.WorkItem.RequestOneofCase.OrchestratorRequest)
                    {
                        this.RunBackgroundTask(workItem, () => this.OnRunOrchestratorAsync(workItem.OrchestratorRequest));
                    }
                    else if (workItem.RequestCase == P.WorkItem.RequestOneofCase.ActivityRequest)
                    {
                        this.RunBackgroundTask(workItem, () => this.OnRunActivityAsync(workItem.ActivityRequest));
                    }
                    else
                    {
                        this.logger.UnexpectedWorkItemType(workItem.RequestCase.ToString());
                    }
                }
            }
            catch (RpcException) when (shutdownToken.IsCancellationRequested)
            {
                // Worker is shutting down - let the method exit gracefully
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                // Sidecar is shutting down - retry
                this.logger.SidecarDisconnected(this.sidecarGrpcChannel.Target);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                // Sidecar is down - keep retrying
                this.logger.SidecarUnavailable(this.sidecarGrpcChannel.Target);
            }
            catch (Exception ex)
            {
                // Unknown failure - retry?
                this.logger.UnexpectedError(instanceId: string.Empty, details: ex.ToString());
            }

            try
            {
                // CONSIDER: Exponential backoff
                await Task.Delay(TimeSpan.FromSeconds(5), shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Worker is shutting down - let the method exit gracefully
                break;
            }

            reconnect = true;
        }
    }

    void RunBackgroundTask(P.WorkItem? workItem, Func<Task> handler)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await handler();
            }
            catch (OperationCanceledException)
            {
                // Shutting down - ignore
            }
            catch (Exception e)
            {
                string instanceId =
                    workItem?.OrchestratorRequest?.InstanceId ??
                    workItem?.ActivityRequest?.OrchestrationInstance?.InstanceId ??
                    string.Empty;
                this.logger.UnexpectedError(instanceId, e.ToString());
            }
        });
    }

    async Task OnRunOrchestratorAsync(P.OrchestratorRequest request)
    {
        OrchestrationRuntimeState runtimeState = BuildRuntimeState(request);
        TaskName name = new(runtimeState.Name, runtimeState.Version);

        this.logger.ReceivedOrchestratorRequest(name, request.InstanceId);

        OrchestratorExecutionResult result;

        try
        {
            TaskOrchestration orchestrator;
            if (this.orchestrators.TryGetValue(name, out Func<WorkerContext, TaskOrchestration>? factory) && factory != null)
            {
                // Both the factory invocation and the ExecuteAsync could involve user code and need to be handled as part of try/catch.
                orchestrator = factory.Invoke(this.workerContext);
                TaskOrchestrationExecutor executor = new(runtimeState, orchestrator, BehaviorOnContinueAsNew.Carryover);
                result = await executor.ExecuteAsync();
            }
            else
            {
                result = this.CreateOrchestrationFailedActionResult($"No task orchestration named '{name}' was found.");
            }
        }
        catch (Exception applicationException)
        {
            this.logger.OrchestratorFailed(name, request.InstanceId, applicationException.ToString());
            result = this.CreateOrchestrationFailedActionResult(applicationException);
        }

        // TODO: This is a workaround that allows us to change how the exception is presented to the user.
        //       Need to move this workaround into DurableTask.Core as a breaking change.
        if (result.Actions.FirstOrDefault(a => a.OrchestratorActionType == OrchestratorActionType.OrchestrationComplete) is OrchestrationCompleteOrchestratorAction completedAction &&
            completedAction.OrchestrationStatus == OrchestrationStatus.Failed &&
            !string.IsNullOrEmpty(completedAction.Details))
        {
            completedAction.Result = SdkUtils.GetSerializedErrorPayload(
                this.workerContext.DataConverter,
                "The orchestrator failed with an unhandled exception.",
                completedAction.Details);
        }

        P.OrchestratorResponse response = ProtoUtils.ConstructOrchestratorResponse(
            request.InstanceId,
            result.CustomStatus,
            result.Actions);

        this.logger.SendingOrchestratorResponse(name, response.InstanceId, response.Actions.Count);
        await this.sidecarClient.CompleteOrchestratorTaskAsync(response);
    }

    OrchestratorExecutionResult CreateOrchestrationFailedActionResult(Exception e)
    {
        return this.CreateOrchestrationFailedActionResult(
            message: "The orchestrator failed with an unhandled exception.",
            fullText: e.ToString());
    }

    OrchestratorExecutionResult CreateOrchestrationFailedActionResult(string message, string? fullText = null)
    {
        return OrchestratorExecutionResult.ForFailure(message, fullText);
    }

    string CreateActivityFailedOutput(Exception? e = null, string? message = null)
    {
        return SdkUtils.GetSerializedErrorPayload(
            this.dataConverter,
            message ?? "The activity failed with an unhandled exception.",
            e);
    }

    async Task OnRunActivityAsync(P.ActivityRequest request)
    {
        OrchestrationInstance instance = ProtoUtils.ConvertOrchestrationInstance(request.OrchestrationInstance);
        string rawInput = request.Input;

        int inputSize = rawInput != null ? Encoding.UTF8.GetByteCount(rawInput) : 0;
        this.logger.ReceivedActivityRequest(request.Name, request.TaskId, instance.InstanceId, inputSize);

        TaskContext innerContext = new(instance);

        TaskName name = new(request.Name, request.Version);

        string output;
        try
        {
            if (this.activities.TryGetValue(name, out Func<WorkerContext, TaskActivity>? factory) && factory != null)
            {
                // Both the factory invocation and the RunAsync could involve user code and need to be handled as part of try/catch.
                TaskActivity activity = factory.Invoke(this.workerContext);
                output = await activity.RunAsync(innerContext, request.Input);
            }
            else if (this.workerContext.DynamicActivities.TryGetValue(name, out TaskActivity? activity))
            {
                // TODO: Need to do work in the worker that ensures 
                output = await activity.RunAsync(innerContext, request.Input);
            }
            else
            {
                output = this.CreateActivityFailedOutput(message: $"No task activity named '{name}' was found.");
            }
        }
        catch (Exception applicationException)
        {
            output = this.CreateActivityFailedOutput(
                applicationException,
                $"The activity '{name}#{request.TaskId}' failed with an unhandled exception.");
        }

        int outputSize = output != null ? Encoding.UTF8.GetByteCount(output) : 0;
        this.logger.SendingActivityResponse(name, request.TaskId, instance.InstanceId, outputSize);

        P.ActivityResponse response = ProtoUtils.ConstructActivityResponse(
            instance.InstanceId,
            request.TaskId,
            output);
        await this.sidecarClient.CompleteActivityTaskAsync(response);
    }

    static OrchestrationRuntimeState BuildRuntimeState(P.OrchestratorRequest request)
    {
        IEnumerable<HistoryEvent> pastEvents = request.PastEvents.Select(ProtoUtils.ConvertHistoryEvent);
        IEnumerable<HistoryEvent> newEvents = request.NewEvents.Select(ProtoUtils.ConvertHistoryEvent);

        // Reconstruct the orchestration state in a way that correctly distinguishes new events from past events
        var runtimeState = new OrchestrationRuntimeState(pastEvents.ToList());
        foreach (HistoryEvent e in newEvents)
        {
            // AddEvent() puts events into the NewEvents list.
            runtimeState.AddEvent(e);
        }

        if (runtimeState.ExecutionStartedEvent == null)
        {
            // TODO: What's the right way to handle this? Callback to the sidecar with a retriable error request?
            throw new InvalidOperationException("The provided orchestration history was incomplete");
        }

        return runtimeState;
    }

    public static Builder CreateBuilder() => new();

    public sealed class Builder
    {
        internal DefaultTaskBuilder taskProvider = new();
        internal ILoggerFactory? loggerFactory;
        internal IDataConverter? dataConverter;
        internal IServiceProvider? services;
        internal IConfiguration? configuration;
        internal string? address;

        internal Builder()
        {
        }

        public DurableTaskGrpcWorker Build() => new(this);

        public Builder UseAddress(string address)
        {
            this.address = SdkUtils.ValidateAddress(address);
            return this;
        }

        public Builder UseLoggerFactory(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return this;
        }

        public Builder UseDataConverter(IDataConverter dataConverter)
        {
            this.dataConverter = dataConverter ?? throw new ArgumentNullException(nameof(dataConverter));
            return this;
        }

        public Builder UseServices(IServiceProvider services)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            return this;
        }

        public Builder UseConfiguration(IConfiguration configuration)
        {
            this.configuration = configuration;
            return this;
        }

        public Builder AddTasks(Action<ITaskBuilder> taskProviderAction)
        {
            taskProviderAction(this.taskProvider);
            return this;
        }

        internal sealed class DefaultTaskBuilder : ITaskBuilder
        {
            internal ImmutableDictionary<TaskName, Func<WorkerContext, TaskActivity>>.Builder activitiesBuilder =
                ImmutableDictionary.CreateBuilder<TaskName, Func<WorkerContext, TaskActivity>>();

            internal ImmutableDictionary<TaskName, Func<WorkerContext, TaskOrchestration>>.Builder orchestratorsBuilder =
                ImmutableDictionary.CreateBuilder<TaskName, Func<WorkerContext, TaskOrchestration>>();

            public ITaskBuilder AddOrchestrator(
                TaskName name,
                Func<TaskOrchestrationContext, Task> implementation)
            {
                return this.AddOrchestrator<object?>(name, async ctx =>
                {
                    await implementation(ctx);
                    return null;
                });
            }

            public ITaskBuilder AddOrchestrator<T>(
                TaskName name,
                Func<TaskOrchestrationContext, Task<T?>> implementation)
            {
                if (name == default)
                {
                    throw new ArgumentNullException(nameof(name));
                }

                if (implementation == null)
                {
                    throw new ArgumentNullException(nameof(implementation));
                }

                if (this.orchestratorsBuilder.ContainsKey(name))
                {
                    throw new ArgumentException($"A task orchestrator named '{name}' is already added.", nameof(name));
                }

                this.orchestratorsBuilder.Add(
                    name,
                    workerContext => new TaskOrchestrationWrapper<T>(workerContext, name, implementation));

                return this;
            }

            public ITaskBuilder AddOrchestrator<TOrchestrator>() where TOrchestrator : ITaskOrchestrator
            {
                string name = GetTaskName(typeof(TOrchestrator));
                this.orchestratorsBuilder.Add(
                    name,
                    workerContext =>
                    {
                        // Unlike activities, we don't give orchestrators access to the IServiceProvider collection since
                        // injected services are inherently non-deterministic. If an orchestrator needs access to a service,
                        // it should invoke that service through an activity call.
                        TOrchestrator orchestrator = Activator.CreateInstance<TOrchestrator>();
                        return new TaskOrchestrationWrapper<object?>(workerContext, name, orchestrator.RunAsync);
                    });
                return this;
            }

            public ITaskBuilder AddActivity(
                TaskName name,
                Func<ITaskActivityContext, object?> implementation)
            {
                return this.AddActivity(name, context => Task.FromResult(implementation(context)));
            }

            public ITaskBuilder AddActivity(
                TaskName name,
                Func<ITaskActivityContext, Task> implementation)
            {
                return this.AddActivity<object?>(name, async context =>
                {
                    await implementation(context);
                    return null;
                });
            }

            public ITaskBuilder AddActivity<T>(
                TaskName name,
                Func<ITaskActivityContext, Task<T?>> implementation)
            {
                if (name == default)
                {
                    throw new ArgumentNullException(nameof(name));
                }

                if (implementation == null)
                {
                    throw new ArgumentNullException(nameof(implementation));
                }

                if (this.activitiesBuilder.ContainsKey(name))
                {
                    throw new ArgumentException($"A task activity named '{name}' is already added.", nameof(name));
                }

                this.activitiesBuilder.Add(
                    name,
                    workerContext => new TaskActivityWrapper<T>(workerContext, name, implementation));
                return this;
            }

            public ITaskBuilder AddActivity<TActivity>() where TActivity : ITaskActivity
            {
                string name = GetTaskName(typeof(TActivity));
                this.activitiesBuilder.Add(
                    name,
                    workerContext =>
                    {
                        TActivity activity = ActivatorUtilities.CreateInstance<TActivity>(workerContext.Services);
                        return new TaskActivityWrapper<object?>(workerContext, name, activity.RunAsync);
                    });
                return this;
            }

            static TaskName GetTaskName(Type taskDeclarationType)
            {
                // IMPORTANT: This logic needs to be kept consistent with the source generator logic
                DurableTaskAttribute? attribute = (DurableTaskAttribute?)Attribute.GetCustomAttribute(taskDeclarationType, typeof(DurableTaskAttribute));
                if (attribute != null)
                {
                    return attribute.Name;
                }
                else
                {
                    return taskDeclarationType.Name;
                }
            }
        }
    }

    internal record WorkerContext(
        IDataConverter DataConverter,
        ILogger Logger,
        IServiceProvider Services,
        ConcurrentDictionary<string, TaskActivity> DynamicActivities) : IWorkerContext;
}