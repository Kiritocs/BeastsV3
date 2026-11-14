namespace BeastsV3.Beasts;

// Shared definition of a completed map, used by the counter overlay and the recorder.
public static class MapCompletion
{
    // Complete when the quest tracker says so, or when the quest's red beast count is met and
    // every tracked beast is captured.
    public static bool IsComplete(bool missionCompleteText, int questTotal, int redBeastsFound,
        bool allTrackedCaptured) =>
        missionCompleteText ||
        (questTotal > 0 && redBeastsFound >= questTotal && allTrackedCaptured);
}
