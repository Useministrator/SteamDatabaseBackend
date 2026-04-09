/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    internal static class TaskManager
    {
        private static readonly ConcurrentDictionary<Task, byte> Tasks = new ConcurrentDictionary<Task, byte>();
        public static CancellationTokenSource TaskCancellationToken { get; } = new CancellationTokenSource();

        public static int TasksCount => Tasks.Count;

        public static Task<TResult> Run<TResult>(Func<Task<TResult>> function)
        {
            var t = Task.Run(function, TaskCancellationToken.Token);

            AddTask(t);

            return t;
        }

        public static Task Run(Func<Task> action)
        {
            var t = Task.Run(action, TaskCancellationToken.Token);

            AddTask(t);

            return t;
        }

        private static void AddTask(Task t)
        {
            Tasks.TryAdd(t, 1);

            t.ContinueWith(task =>
            {
                task.Exception?.Flatten().Handle(e =>
                {
                    ErrorReporter.Notify(nameof(TaskManager), e);

                    return false;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);

            t.ContinueWith(task => Tasks.TryRemove(task, out _));
        }

        public static void CancelAllTasks()
        {
            Log.WriteInfo(nameof(TaskManager), $"Cancelling {TasksCount} tasks...");

            if (!TaskCancellationToken.IsCancellationRequested)
            {
                TaskCancellationToken.Cancel();
            }
        }

        public static void WaitForDrain(TimeSpan timeout, TimeSpan? pollInterval = null)
        {
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                var pendingTasks = Tasks.Keys.Where(task => !task.IsCompleted).ToArray();

                if (pendingTasks.Length == 0)
                {
                    Log.WriteInfo(nameof(TaskManager), $"All tasks drained in {stopwatch.Elapsed.TotalSeconds:0.0}s");

                    return;
                }

                try
                {
                    Task.WaitAny(pendingTasks, interval);
                }
                catch (AggregateException)
                {
                    // Individual task failures are already routed through ErrorReporter.
                }
            }

            var remainingTasks = Tasks.Keys.Count(task => !task.IsCompleted);

            if (remainingTasks > 0)
            {
                Log.WriteWarn(nameof(TaskManager), $"Timed out waiting for {remainingTasks} task(s) to drain after {timeout.TotalSeconds:0}s");
            }
        }
    }
}
