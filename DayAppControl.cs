using Tizen.Applications;

namespace GoalFlow.Device;

/// <summary>
/// TIZEN EDGE (v10.0a): tell ANOTHER on-Hub app that the world moved a day.
///
/// <para>
/// When the board's "advance day" reaches this service as a goal-less
/// <c>advance_day</c> control, the world tick runs and this fires ONE App Control at a
/// configured application, carrying a single string — <c>"day 1"</c>, <c>"day 2"</c>,
/// <c>"day 3"</c> — under the <see cref="DayKey"/> extra. Everything the receiver does
/// with it is the receiver's business; this side has no reply, no callback and no
/// expectation that the app even exists.
/// </para>
///
/// <para>
/// WHY THIS IS A SEPARATE FILE, AND WHY IT TOUCHES NO CORE TYPE. The portable core in
/// <c>Agent/ Contracts/ Harness/ Products/ Transport/</c> is a byte-identical copy of
/// <c>../goal-flow-device-agent-ubuntu</c> and every re-sync re-copies it wholesale; a
/// device-only side effect written into a core file would be silently deleted by the
/// next sync. So the whole feature lives here plus ONE guarded line in
/// <c>Program.cs</c>, both of which are host files on the never-overwrite list. The
/// <c>diff -rq</c> core check in AGENTS.md stays clean, and Ubuntu needs no counterpart
/// — there is nothing to mirror because nothing shared changed. This is the same shape
/// the old <c>UiChannel.cs</c> used to drive <c>org.goalflow.tizenui</c>, which was
/// verified on real Family Hub hardware.
/// </para>
///
/// <para>
/// FAILURE IS NOT THE TICK'S PROBLEM. A missing target app, a revoked privilege or a
/// crashed receiver throws out of <see cref="AppControl.SendLaunchRequest(AppControl)"/>;
/// all of it is caught and dlogged here, because a demo prop must never be able to take
/// down the world tick that the board is waiting on.
/// </para>
/// </summary>
public sealed class DayAppControl
{
    /// <summary>
    /// <c>goalflow.conf</c> key naming the receiving application (e.g.
    /// <c>DAY_APPCONTROL_APPID=org.example.dayviewer</c>). UNSET = the feature is off,
    /// which is the default: a Hub that has no such app installed should send nothing
    /// rather than throw once per day. Config, not a constant, so retargeting the
    /// receiver is a conf edit on the Hub and not a rebuild.
    /// </summary>
    public const string AppIdKey = "DAY_APPCONTROL_APPID";

    /// <summary>The single extra the receiver reads: <c>ExtraData["day"] == "day 3"</c>.</summary>
    public const string DayKey = "day";

    private readonly string? _appId;

    /// <summary>
    /// Ticks since start — the fallback day number. <see cref="Contracts.DayAdvanced.Day"/>
    /// is measured from the earliest ACTIVE goal's window start, so it is 0 when no goal is
    /// running; the receiver still wants a sensible "day N" then. Incremented on every fire
    /// and used only when the real day is unavailable.
    /// </summary>
    private int _ticks;

    public DayAppControl(DeviceConfig config) => _appId = config.Get(AppIdKey);

    /// <summary>False when <see cref="AppIdKey"/> is unconfigured — nothing is ever sent.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_appId);

    /// <summary>
    /// Fire the day App Control. <paramref name="day"/> is the world tick's 1-based sim
    /// day (<c>DayAdvanced.Day</c>); pass 0 when there is none and the local tick count is
    /// used instead. Launches the target app if it is not already running.
    /// </summary>
    public void Fire(int day)
    {
        var n = Interlocked.Increment(ref _ticks);
        if (!Enabled)
        {
            return;
        }

        var value = $"day {(day > 0 ? day : n)}";
        try
        {
            // NOT IDisposable — no `using` (an on-Hub gotcha from the UiChannel work).
            var request = new AppControl
            {
                ApplicationId = _appId,
                Operation = AppControlOperations.Default
            };
            request.ExtraData.Add(DayKey, value);
            AppControl.SendLaunchRequest(request);
            Tizen.Log.Info(DlogLoggerProvider.Tag, $"day_appcontrol sent app_id={_appId} {DayKey}=\"{value}\"");
        }
        catch (Exception ex)
        {
            Tizen.Log.Error(DlogLoggerProvider.Tag, $"day_appcontrol FAILED app_id={_appId} {DayKey}=\"{value}\": {ex}");
        }
    }

    /// <summary>
    /// A world <c>reset</c> restores the mock world and clears every goal, so the fallback
    /// counter goes back to the start too — otherwise the first tick after a reset would
    /// announce "day 7" to an app that is showing day one of a fresh demo.
    /// </summary>
    public void Reset() => Interlocked.Exchange(ref _ticks, 0);
}
