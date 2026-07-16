using System;
using System.Threading;
using System.Threading.Tasks;

namespace TerrainLab
{
    public sealed class TerrainReliefService
    {
        private readonly object _sync = new object();
        private Task<TerrainReliefResult> _task;
        private CancellationTokenSource _cancellation;
        private int _generation;
        private int _taskGeneration;
        private int _progressPercent;
        private string _lastError;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _task != null;
                }
            }
        }

        public int ProgressPercent => Volatile.Read(ref _progressPercent);

        public string LastError
        {
            get
            {
                lock (_sync)
                {
                    return _lastError;
                }
            }
        }

        public bool TryStartAnalysis(TerrainWorldState state, out string error)
        {
            error = null;
            if (state == null)
            {
                error = "Relief analysis requires an active TerrainLab project.";
                return false;
            }

            lock (_sync)
            {
                if (_task != null)
                {
                    error = "Relief analysis is already running.";
                    return false;
                }

                int generation = ++_generation;
                _taskGeneration = generation;
                _progressPercent = 0;
                _lastError = null;
                _cancellation = new CancellationTokenSource();
                CancellationToken token = _cancellation.Token;
                string projectId = state.ProjectId;
                long revision = state.Revision;
                int width = state.Width;
                int height = state.Height;
                double horizontalMetresPerCell = state.HorizontalMetresPerCell;
                short[] elevation = (short[])state.Elevation.Clone();
                _task = Task.Run(
                    () => TerrainReliefAnalyzer.Analyze(
                        projectId,
                        revision,
                        width,
                        height,
                        elevation,
                        horizontalMetresPerCell,
                        value =>
                        {
                            if (Volatile.Read(ref _generation) == generation)
                            {
                                Interlocked.Exchange(ref _progressPercent, value);
                            }
                        },
                        token),
                    token);
                return true;
            }
        }

        public bool Poll(TerrainWorldState state)
        {
            Task<TerrainReliefResult> task;
            int generation;
            lock (_sync)
            {
                task = _task;
                generation = _taskGeneration;
                if (task == null || !task.IsCompleted)
                {
                    return false;
                }
            }

            TerrainReliefResult result = null;
            string error = null;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                error = "Relief analysis was cancelled.";
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            lock (_sync)
            {
                if (!ReferenceEquals(task, _task) || generation != _taskGeneration)
                {
                    return false;
                }

                _task = null;
                _cancellation?.Dispose();
                _cancellation = null;
                _lastError = error;
            }

            if (result != null)
            {
                if (result.IsCurrent(state))
                {
                    state.Relief = result;
                }
                else
                {
                    lock (_sync)
                    {
                        _lastError = "DEM changed while relief analysis was running; result discarded.";
                    }
                }
            }

            return true;
        }

        public void Cancel()
        {
            lock (_sync)
            {
                _cancellation?.Cancel();
            }
        }
    }
}
