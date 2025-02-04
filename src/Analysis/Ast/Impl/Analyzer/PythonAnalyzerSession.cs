﻿// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Analysis.Dependencies;
using Microsoft.Python.Analysis.Diagnostics;
using Microsoft.Python.Analysis.Modules;
using Microsoft.Python.Analysis.Types;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Logging;
using Microsoft.Python.Core.Services;

namespace Microsoft.Python.Analysis.Analyzer {
    internal sealed class PythonAnalyzerSession {
        private readonly int _maxTaskRunning = Environment.ProcessorCount;
        private readonly object _syncObj = new object();

        private IDependencyChainWalker<AnalysisModuleKey, PythonAnalyzerEntry> _walker;
        private readonly PythonAnalyzerEntry _entry;
        private readonly Action<Task> _startNextSession;
        private readonly CancellationToken _analyzerCancellationToken;
        private readonly IServiceManager _services;
        private readonly AsyncManualResetEvent _analysisCompleteEvent;
        private readonly IDiagnosticsService _diagnosticsService;
        private readonly IProgressReporter _progress;
        private readonly IPythonAnalyzer _analyzer;
        private readonly ILogger _log;
        private readonly ITelemetryService _telemetry;

        private State _state;
        private bool _isCanceled;
        private int _runningTasks;

        public bool IsCompleted {
            get {
                lock (_syncObj) {
                    return _state == State.Completed;
                }
            }
        }

        public int Version { get; }
        public int AffectedEntriesCount { get; }

        public PythonAnalyzerSession(IServiceManager services,
            IProgressReporter progress,
            AsyncManualResetEvent analysisCompleteEvent,
            Action<Task> startNextSession,
            CancellationToken analyzerCancellationToken,
            IDependencyChainWalker<AnalysisModuleKey, PythonAnalyzerEntry> walker,
            int version,
            PythonAnalyzerEntry entry) {

            _services = services;
            _analysisCompleteEvent = analysisCompleteEvent;
            _startNextSession = startNextSession;
            _analyzerCancellationToken = analyzerCancellationToken;
            Version = version;
            AffectedEntriesCount = walker?.AffectedValues.Count ?? 1;
            _walker = walker;
            _entry = entry;
            _state = State.NotStarted;

            _diagnosticsService = _services.GetService<IDiagnosticsService>();
            _analyzer = _services.GetService<IPythonAnalyzer>();
            _log = _services.GetService<ILogger>();
            _telemetry = _services.GetService<ITelemetryService>();
            _progress = progress;
        }

        public void Start(bool analyzeEntry) {
            lock (_syncObj) {
                if (_state != State.NotStarted) {
                    analyzeEntry = false;
                } else if (_state == State.Completed) {
                    return;
                } else {
                    _state = State.Started;
                }
            }

            if (analyzeEntry && _entry != null) {
                Task.Run(() => AnalyzeEntry(), _analyzerCancellationToken).DoNotWait();
            } else {
                StartAsync().ContinueWith(_startNextSession, _analyzerCancellationToken).DoNotWait();
            }
        }

        public void Cancel() {
            lock (_syncObj) {
                _isCanceled = true;
            }
        }

        private async Task StartAsync() {
            _progress.ReportRemaining(_walker.Remaining);

            lock (_syncObj) {
                var notAnalyzed = _walker.AffectedValues.Count(e => e.NotAnalyzed);

                if (_isCanceled && notAnalyzed < _maxTaskRunning) {
                    _state = State.Completed;
                    return;
                }
            }

            var stopWatch = Stopwatch.StartNew();
            foreach (var affectedEntry in _walker.AffectedValues) {
                affectedEntry.Invalidate(Version);
            }

            var originalRemaining = _walker.Remaining;
            var remaining = originalRemaining;
            try {
                _log?.Log(TraceEventType.Verbose, $"Analysis version {Version} of {originalRemaining} entries has started.");
                remaining = await AnalyzeAffectedEntriesAsync(stopWatch);
            } finally {
                stopWatch.Stop();

                bool isCanceled;
                bool isFinal;
                lock (_syncObj) {
                    isCanceled = _isCanceled;
                    _state = State.Completed;
                    isFinal = _walker.MissingKeys.Count == 0 && !isCanceled && remaining == 0;
                    _walker = null;
                }

                if (!isCanceled) {
                    _progress.ReportRemaining(remaining);
                    if (isFinal) {
                        ActivityTracker.EndTracking();
                        (_analyzer as PythonAnalyzer)?.RaiseAnalysisComplete(ActivityTracker.ModuleCount, ActivityTracker.MillisecondsElapsed);
                        _log?.Log(TraceEventType.Verbose, $"Analysis complete: {ActivityTracker.ModuleCount} modules in { ActivityTracker.MillisecondsElapsed} ms.");
                    }
                }
            }

            var elapsed = stopWatch.Elapsed.TotalMilliseconds;
            LogResults(_log, elapsed, originalRemaining, remaining, Version);
            ForceGCIfNeeded(originalRemaining, remaining);
        }

        private static void ForceGCIfNeeded(int originalRemaining, int remaining) {
            if (originalRemaining - remaining > 1000) {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
        }


        private static void LogResults(ILogger logger, double elapsed, int originalRemaining, int remaining, int version) {
            if (logger == null) {
                return;
            }

            if (remaining == 0) {
                logger.Log(TraceEventType.Verbose, $"Analysis version {version} of {originalRemaining} entries has been completed in {elapsed} ms.");
            } else if (remaining < originalRemaining) {
                logger.Log(TraceEventType.Verbose, $"Analysis version {version} has been completed in {elapsed} ms with {originalRemaining - remaining} entries analyzed and {remaining} entries skipped.");
            } else {
                logger.Log(TraceEventType.Verbose, $"Analysis version {version} of {originalRemaining} entries has been canceled after {elapsed}.");
            }
        }

        private async Task<int> AnalyzeAffectedEntriesAsync(Stopwatch stopWatch) {
            IDependencyChainNode<PythonAnalyzerEntry> node;
            var remaining = 0;
            var ace = new AsyncCountdownEvent(0);

            bool isCanceled;
            while ((node = await _walker.GetNextAsync(_analyzerCancellationToken)) != null) {
                lock (_syncObj) {
                    isCanceled = _isCanceled;
                }

                if (isCanceled && !node.Value.NotAnalyzed) {
                    remaining++;
                    node.Skip();
                    continue;
                }

                ActivityTracker.OnEnqueueModule(node.Value.Module.FilePath);

                if (Interlocked.Increment(ref _runningTasks) >= _maxTaskRunning || _walker.Remaining == 1) {
                    Analyze(node, null, stopWatch);
                } else {
                    StartAnalysis(node, ace, stopWatch).DoNotWait();
                }
            }

            await ace.WaitAsync(_analyzerCancellationToken);

            lock (_syncObj) {
                isCanceled = _isCanceled;
            }

            if (_walker.MissingKeys.Count == 0 || _walker.MissingKeys.All(k => k.IsTypeshed)) {
                Interlocked.Exchange(ref _runningTasks, 0);

                if (!isCanceled) {
                    _analysisCompleteEvent.Set();
                }
            } else if (!isCanceled && _log != null && _log.LogLevel >= TraceEventType.Verbose) {
                _log?.Log(TraceEventType.Verbose, $"Missing keys: {string.Join(", ", _walker.MissingKeys)}");
            }

            return remaining;
        }


        private Task StartAnalysis(IDependencyChainNode<PythonAnalyzerEntry> node, AsyncCountdownEvent ace, Stopwatch stopWatch)
            => Task.Run(() => Analyze(node, ace, stopWatch));

        /// <summary>
        /// Performs analysis of the document. Returns document global scope
        /// with declared variables and inner scopes. Does not analyze chain
        /// of dependencies, it is intended for the single file analysis.
        /// </summary>
        private void Analyze(IDependencyChainNode<PythonAnalyzerEntry> node, AsyncCountdownEvent ace, Stopwatch stopWatch) {
            try {
                ace?.AddOne();
                var entry = node.Value;

                if (!entry.IsValidVersion(_walker.Version, out var module, out var ast)) {
                    if (ast == null) {
                        // Entry doesn't have ast yet. There should be at least one more session.
                        Cancel();
                    }

                    _log?.Log(TraceEventType.Verbose, $"Analysis of {module.Name}({module.ModuleType}) canceled.");
                    node.Skip();
                    return;
                }

                var startTime = stopWatch.Elapsed;
                AnalyzeEntry(entry, module, _walker.Version, node.IsComplete);
                node.Commit();
                ActivityTracker.OnModuleAnalysisComplete(node.Value.Module.FilePath);

                LogCompleted(module, stopWatch, startTime);
            } catch (OperationCanceledException oce) {
                node.Value.TryCancel(oce, _walker.Version);
                node.Skip();
                LogCanceled(node.Value.Module);
            } catch (Exception exception) {
                node.Value.TrySetException(exception, _walker.Version);
                node.Commit();
                LogException(node.Value.Module, exception);
            } finally {
                bool isCanceled;
                lock (_syncObj) {
                    isCanceled = _isCanceled;
                }

                if (!isCanceled) {
                    _progress.ReportRemaining(_walker.Remaining);
                }

                Interlocked.Decrement(ref _runningTasks);
                ace?.Signal();
            }
        }

        private void AnalyzeEntry() {
            var stopWatch = _log != null ? Stopwatch.StartNew() : null;
            try {
                if (!_entry.IsValidVersion(Version, out var module, out var ast)) {
                    if (ast == null) {
                        // Entry doesn't have ast yet. There should be at least one more session.
                        Cancel();
                    }
                    _log?.Log(TraceEventType.Verbose, $"Analysis of {module.Name}({module.ModuleType}) canceled.");
                    return;
                }

                var startTime = stopWatch?.Elapsed ?? TimeSpan.Zero;

                AnalyzeEntry(_entry, module, Version, true);

                LogCompleted(module, stopWatch, startTime);
            } catch (OperationCanceledException oce) {
                _entry.TryCancel(oce, Version);
                LogCanceled(_entry.Module);
            } catch (Exception exception) {
                _entry.TrySetException(exception, Version);
                LogException(_entry.Module, exception);
            } finally {
                stopWatch?.Stop();
                Interlocked.Decrement(ref _runningTasks);
            }
        }

        private void AnalyzeEntry(PythonAnalyzerEntry entry, IPythonModule module, int version, bool isFinalPass) {
            if (entry.PreviousAnalysis is LibraryAnalysis) {
                _log?.Log(TraceEventType.Verbose, $"Request to re-analyze finalized {module.Name}.");
            }

            // Now run the analysis.
            var analyzable = module as IAnalyzable;
            analyzable?.NotifyAnalysisBegins();

            var ast = module.GetAst();
            var walker = new ModuleWalker(_services, module);
            ast.Walk(walker);

            _analyzerCancellationToken.ThrowIfCancellationRequested();

            walker.Complete();
            _analyzerCancellationToken.ThrowIfCancellationRequested();

            analyzable?.NotifyAnalysisComplete(version, walker, isFinalPass);
            entry.TrySetAnalysis(module.Analysis, version);

            if (module.ModuleType == ModuleType.User) {
                var linterDiagnostics = _analyzer.LintModule(module);
                _diagnosticsService?.Replace(entry.Module.Uri, linterDiagnostics, DiagnosticSource.Linter);
            }
        }

        private void LogCompleted(IPythonModule module, Stopwatch stopWatch, TimeSpan startTime) {
            if (_log != null) {
                _log.Log(TraceEventType.Verbose, $"Analysis of {module.Name}({module.ModuleType}) completed in {(stopWatch.Elapsed - startTime).TotalMilliseconds} ms.");
            }
        }

        private void LogCanceled(IPythonModule module) {
            if (_log != null) {
                _log.Log(TraceEventType.Verbose, $"Analysis of {module.Name}({module.ModuleType}) canceled.");
            }
        }

        private void LogException(IPythonModule module, Exception exception) {
            if (_log != null) {
                _log.Log(TraceEventType.Verbose, $"Analysis of {module.Name}({module.ModuleType}) failed. Exception message: {exception.Message}.");
            }
        }

        private enum State {
            NotStarted = 0,
            Started = 1,
            Completed = 2
        }
    }
}
