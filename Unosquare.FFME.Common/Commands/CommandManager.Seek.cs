﻿namespace Unosquare.FFME.Commands
{
    using Primitives;
    using Shared;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    internal partial class CommandManager
    {
        #region State Backing Fields

        private readonly ManualResetEventSlim SeekBlocksAvailable = new ManualResetEventSlim(true);
        private readonly AtomicBoolean m_IsSeeking = new AtomicBoolean(false);
        private readonly AtomicBoolean m_PlayAfterSeek = new AtomicBoolean(false);

        private SeekOperation QueuedSeekOperation = null;
        private Task<bool> QueuedSeekTask;

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether a seek operation is pending or in progress.
        /// </summary>
        public bool IsSeeking
        {
            get => m_IsSeeking.Value;
            private set => m_IsSeeking.Value = value;
        }

        /// <summary>
        /// Gets or sets a value indicating whether playback should be resumed when all
        /// seek operations complete.
        /// </summary>
        private bool PlayAfterSeek
        {
            get => m_PlayAfterSeek.Value;
            set => m_PlayAfterSeek.Value = value;
        }

        #endregion

        #region Command Implementations

        /// <summary>
        /// Executes boilerplate logic to queue a seek operation.
        /// </summary>
        /// <param name="seekTarget">The seek target.</param>
        /// <param name="seekMode">The seek mode.</param>
        /// <returns>An awaitable task</returns>
        private Task<bool> QueueSeekCommand(TimeSpan seekTarget, SeekMode seekMode)
        {
            lock (SyncLock)
            {
                if (IsDisposed || IsDisposing || MediaCore.State.IsOpen == false ||
                    HasPendingDirectCommands || PendingPriorityCommand != PriorityCommandType.None)
                    return Task.FromResult(false);

                if (QueuedSeekTask != null)
                {
                    QueuedSeekOperation.Mode = seekMode;
                    QueuedSeekOperation.Position = seekTarget;
                    return QueuedSeekTask;
                }

                if (IsSeeking == false)
                {
                    IsSeeking = true;
                    PlayAfterSeek = State.MediaState == PlaybackStatus.Play && seekMode == SeekMode.Normal;
                    MediaCore.Clock.Pause();
                    MediaCore.State.UpdateMediaState(PlaybackStatus.Manual);
                    MediaCore.SendOnSeekingStarted();
                }

                var seekOperation = new SeekOperation(seekTarget, seekMode);
                QueuedSeekOperation = seekOperation;
                QueuedSeekTask = new Task<bool>(() =>
                {
                    seekOperation.Wait();
                    return true;
                });

                QueuedSeekTask.ConfigureAwait(false);
                QueuedSeekTask.Start();

                return QueuedSeekTask;
            }
        }

        /// <summary>
        /// Clears the queued seek commands.
        /// </summary>
        private void ClearSeekCommands()
        {
            lock (SyncLock)
            {
                QueuedSeekOperation?.Dispose();
                QueuedSeekOperation = null;
                QueuedSeekTask = null;
                IsSeeking = false;
            }
        }

        /// <summary>
        /// Implements the Seek Media Command.
        /// </summary>
        /// <param name="seekOperation">The seek operation.</param>
        /// <param name="ct">The ct.</param>
        /// <returns>True if the operation was successful</returns>
        private bool SeekMedia(SeekOperation seekOperation, CancellationToken ct)
        {
            // TODO: Handle Cancellation token ct
            var result = false;
            var initialPosition = MediaCore.WallClock;
            var hasDecoderSeeked = false;
            var startTime = DateTime.UtcNow;
            var targetSeekMode = seekOperation.Mode;
            var targetPosition = seekOperation.Position;

            try
            {
                var main = MediaCore.Container.Components.MainMediaType;
                var all = MediaCore.Container.Components.MediaTypes;
                var mainBlocks = MediaCore.Blocks[main];

                if (targetSeekMode == SeekMode.StepBackward || targetSeekMode == SeekMode.StepForward)
                {
                    var neighbors = mainBlocks.Neighbors(initialPosition);
                    targetPosition = neighbors[targetSeekMode == SeekMode.StepBackward ? 0 : 1]?.StartTime ??
                        TimeSpan.FromTicks(neighbors[2].StartTime.Ticks - Convert.ToInt64(neighbors[2].Duration.Ticks / 2d));
                }
                else if (targetSeekMode == SeekMode.Stop)
                {
                    targetPosition = TimeSpan.Zero;
                }

                // Check if we already have the block. If we do, simply set the clock position to the target position
                // we don't need anything else. This implements frame-by frame seeking and we need to snap to a discrete
                // position of the main component so it sticks on it.
                if (mainBlocks.IsInRange(targetPosition))
                {
                    MediaCore.ChangePosition(targetPosition);
                    return true;
                }

                // wait for the current reading and decoding cycles
                // to finish. We don't want to interfere with reading in progress
                // or decoding in progress. For decoding we already know we are not
                // in a cycle because the decoding worker called this logic.
                MediaCore.Workers.Pause(true, true, true, false);

                // Let consumers know main blocks are not avaiable
                hasDecoderSeeked = true;
                SeekBlocksAvailable.Reset();

                // Signal the starting state clearing the packet buffer cache
                MediaCore.Container.Components.ClearQueuedPackets(flushBuffers: true);

                // Capture seek target adjustment
                var adjustedSeekTarget = targetPosition;
                if (targetPosition != TimeSpan.Zero && mainBlocks.IsMonotonic)
                {
                    var targetSkewTicks = Convert.ToInt64(
                        mainBlocks.MonotonicDuration.Ticks * (mainBlocks.Capacity / 2d));

                    if (adjustedSeekTarget.Ticks >= targetSkewTicks)
                        adjustedSeekTarget = TimeSpan.FromTicks(adjustedSeekTarget.Ticks - targetSkewTicks);
                }

                // Populate frame queues with after-seek operation
                var firstFrame = MediaCore.Container.Seek(adjustedSeekTarget);
                if (firstFrame != null)
                {
                    // Ensure we signal media has not ended
                    State.UpdateMediaEnded(false, TimeSpan.Zero);

                    // Clear Blocks and frames, reset the render times
                    foreach (var mt in all)
                    {
                        MediaCore.Blocks[mt].Clear();
                        MediaCore.InvalidateRenderer(mt);
                    }

                    // Create the blocks from the obtained seek frames
                    MediaCore.Blocks[firstFrame.MediaType]?.Add(firstFrame, MediaCore.Container);

                    // Decode all available queued packets into the media component blocks
                    foreach (var mt in all)
                    {
                        while (MediaCore.Blocks[mt].IsFull == false && ct.IsCancellationRequested == false)
                        {
                            var frame = MediaCore.Container.Components[mt].ReceiveNextFrame();
                            if (frame == null) break;
                            MediaCore.Blocks[mt].Add(frame, MediaCore.Container);

                            // signal that thesere is a main block available
                            if (mainBlocks.IsInRange(targetPosition))
                                SeekBlocksAvailable.Set();
                        }
                    }

                    // Align to the exact requested position on the main component
                    while (MediaCore.ShouldReadMorePackets && ct.IsCancellationRequested == false)
                    {
                        // Check if we are already in range
                        if (mainBlocks.IsInRange(targetPosition)) break;

                        // Read the next packet
                        var packetType = MediaCore.Container.Read();
                        var blocks = MediaCore.Blocks[packetType];
                        if (blocks == null) continue;

                        // Get the next frame
                        if (blocks.RangeEndTime.Ticks < targetPosition.Ticks || blocks.IsFull == false)
                            blocks.Add(MediaCore.Container.Components[packetType].ReceiveNextFrame(), MediaCore.Container);
                    }
                }

                // Find out what the final, best-effort position was
                TimeSpan resultPosition;
                if (mainBlocks.IsInRange(targetPosition) == false)
                {
                    // We don't have a a valid main range
                    var minStartTimeTicks = mainBlocks.RangeStartTime.Ticks;
                    var maxStartTimeTicks = mainBlocks.RangeEndTime.Ticks;

                    this.LogWarning(Aspects.EngineCommand,
                        $"SEEK TP: Target Pos {targetPosition.Format()} not between {mainBlocks.RangeStartTime.TotalSeconds:0.000} " +
                        $"and {mainBlocks.RangeEndTime.TotalSeconds:0.000}");

                    resultPosition = TimeSpan.FromTicks(targetPosition.Ticks.Clamp(minStartTimeTicks, maxStartTimeTicks));
                }
                else
                {
                    resultPosition = mainBlocks.Count == 0 && targetPosition != TimeSpan.Zero ?
                        initialPosition : // Unsuccessful. This initial position is simply what the clock was :(
                        targetPosition; // Successful seek with main blocks in range
                }

                // Write a new Real-time clock position now.
                MediaCore.ChangePosition(resultPosition);
            }
            catch (Exception ex)
            {
                // Log the exception
                this.LogError(Aspects.EngineCommand, "SEEK ERROR", ex);
            }
            finally
            {
                if (hasDecoderSeeked)
                {
                    SeekBlocksAvailable.Set();
                    this.LogTrace(Aspects.EngineCommand,
                        $"SEEK D: Elapsed: {startTime.FormatElapsed()} | Target: {targetPosition.Format()}");
                }

                seekOperation.Dispose();
            }

            return result;
        }

        #endregion

        #region Support Classes

        /// <summary>
        /// Provides parameters and a reset event to reference when the operation completes.
        /// </summary>
        /// <seealso cref="IDisposable" />
        private sealed class SeekOperation : IDisposable
        {
            private readonly object SyncLock = new object();
            private bool IsDisposed = false;

            /// <summary>
            /// Initializes a new instance of the <see cref="SeekOperation"/> class.
            /// </summary>
            /// <param name="position">The position.</param>
            /// <param name="mode">The mode.</param>
            public SeekOperation(TimeSpan position, SeekMode mode)
            {
                Position = position;
                Mode = mode;
            }

            /// <summary>
            /// Gets or sets the target position.
            /// </summary>
            public TimeSpan Position { get; set; }

            /// <summary>
            /// Gets or sets the seek mode.
            /// </summary>
            public SeekMode Mode { get; set; }

            /// <summary>
            /// Gets the seek completed event.
            /// </summary>
            private ManualResetEventSlim SeekCompleted { get; } = new ManualResetEventSlim(false);

            /// <summary>
            /// Waits for the <see cref="SeekCompleted"/> event to be set.
            /// </summary>
            public void Wait()
            {
                lock (SyncLock)
                {
                    if (IsDisposed) return;
                }

                SeekCompleted.Wait();
            }

            /// <inheritdoc />
            public void Dispose() => Dispose(true);

            /// <summary>
            /// Releases unmanaged and - optionally - managed resources.
            /// </summary>
            /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
            private void Dispose(bool alsoManaged)
            {
                lock (SyncLock)
                {
                    if (IsDisposed) return;
                    IsDisposed = true;
                }

                SeekCompleted.Set();
                SeekCompleted.Dispose();
            }
        }

        #endregion
    }
}