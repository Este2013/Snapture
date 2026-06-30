using Snapture.Core.Models;

namespace Snapture.Core.Recording;

public sealed class StateChangedEventArgs(RecordingState oldState, RecordingState newState) : EventArgs
{
    public RecordingState OldState { get; } = oldState;
    public RecordingState NewState { get; } = newState;
}
