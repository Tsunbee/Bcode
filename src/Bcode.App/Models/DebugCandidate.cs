namespace Bcode.App.Models;

/// <summary>One candidate the "Debug store/function" picker can debug — an EXEC of a stored
/// procedure or a call to a scalar/table function found somewhere in the current script,
/// already resolved against the database so debugging can jump straight to that object's own
/// definition. Matches FCode's "Chọn store/function để debug" dialog: one row per candidate,
/// tagged by kind, with the line it was found on and the call text as written.</summary>
public class DebugCandidate
{
    public required int Line { get; init; }
    public required SqlObjectInfo Target { get; init; }
    public required string CallText { get; init; }
}
