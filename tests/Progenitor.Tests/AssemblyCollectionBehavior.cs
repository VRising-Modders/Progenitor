using Xunit;

// The suite pins process-wide statics (Plugin.Logger, MainThread, Assets, the Harmony slot tables) and
// finishes in well under a second, so classes run one at a time instead of racing over those statics
// through per-concern [Collection] names that a class can only join once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
