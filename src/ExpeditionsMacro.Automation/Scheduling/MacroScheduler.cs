using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;
using ExpeditionsMacro.Core.Runtime;

namespace ExpeditionsMacro.Automation.Scheduling;

public sealed record ScheduledTaskResult(
    int Victories,
    int Defeats,
    TimeSpan Runtime,
    DateTimeOffset? NextEligibleAtUtc = null,
    bool Skipped = false);

public enum ScheduledTaskContinuation
{
    Handoff,
    RepeatStage,
}

public sealed class MacroScheduler
{
    private readonly MacroPlanRepository _plans;

    public MacroScheduler(MacroPlanRepository plans) => _plans = plans;

    public async Task RunAsync(
        MacroPlan initialPlan,
        Func<
            MacroTaskDefinition,
            Func<ScheduledTaskResult, CancellationToken, Task<ScheduledTaskContinuation>>,
            CancellationToken,
            Task<ScheduledTaskResult>> execute,
        IProgress<MacroProgress>? progress = null,
        Action<MacroPlan>? planChanged = null,
        Action<MacroEvent>? log = null,
        CancellationToken cancellationToken = default)
    {
        initialPlan.Validate();
        MacroPlan plan = NormalizeProgress(initialPlan);
        await SaveAsync(plan, planChanged, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            MacroTaskDefinition? next = SelectNext(plan, now);
            if (next is null)
            {
                DateTimeOffset? wake = NextWake(plan, now);
                string message = wake is null
                    ? "Every finite task is complete. Waiting for the task list to change."
                    : $"No task is currently eligible. Waiting until {wake.Value.LocalDateTime:t}.";
                progress?.Report(new MacroProgress("Waiting", 0, message, "scheduler_waiting"));
                log?.Invoke(new MacroEvent(DateTimeOffset.Now, MacroEventLevel.Information, message, "scheduler_waiting"));
                TimeSpan delay = wake is null
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromMilliseconds(Math.Clamp((wake.Value - now).TotalMilliseconds, 500, 10000));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            progress?.Report(new MacroProgress("Task", 0, $"Starting priority {next.Priority}: {DisplayName(next)}.", "scheduler_task"));
            log?.Invoke(new MacroEvent(DateTimeOffset.Now, MacroEventLevel.Information, $"Scheduler selected priority {next.Priority}: {DisplayName(next)}.", "scheduler_task"));
            MacroTaskDefinition activeTask = next;
            bool resultRecorded = false;

            async Task<ScheduledTaskContinuation> RecordResultAsync(
                ScheduledTaskResult result,
                CancellationToken recordCancellationToken)
            {
                resultRecorded = true;
                DateTimeOffset completedAt = DateTimeOffset.UtcNow;
                MacroTaskProgress before = plan.ProgressFor(activeTask.Id);
                MacroTaskProgress after = Advance(activeTask, before, result, completedAt);
                plan = plan with
                {
                    Progress = plan.Tasks.Select(task => string.Equals(task.Id, activeTask.Id, StringComparison.OrdinalIgnoreCase)
                        ? after
                        : plan.ProgressFor(task.Id)).ToArray(),
                    UpdatedAt = completedAt,
                };
                await SaveAsync(plan, planChanged, recordCancellationToken).ConfigureAwait(false);
                LogResult(activeTask, result, log);

                MacroTaskDefinition? following = SelectNext(plan, DateTimeOffset.UtcNow);
                if (!CanRepeatStage(activeTask, following, result)) return ScheduledTaskContinuation.Handoff;
                log?.Invoke(new MacroEvent(
                    DateTimeOffset.Now,
                    MacroEventLevel.Information,
                    $"Scheduler kept {DisplayName(following!)} on the same route; using Repeat Stage.",
                    "scheduler_repeat_stage"));
                activeTask = following!;
                return ScheduledTaskContinuation.RepeatStage;
            }

            ScheduledTaskResult returned = await execute(next, RecordResultAsync, cancellationToken).ConfigureAwait(false);
            if (!resultRecorded)
            {
                await RecordResultAsync(returned, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<MacroPlan> ResetProgressAsync(MacroPlan plan, CancellationToken cancellationToken = default)
    {
        MacroPlan reset = plan.ResetProgress();
        await _plans.SaveAsync(reset, cancellationToken).ConfigureAwait(false);
        return reset;
    }

    public static MacroTaskDefinition? SelectNext(MacroPlan plan, DateTimeOffset now)
    {
        var eligible = plan.Tasks
            .Select((task, index) => new { Task = task, Index = index, Progress = plan.ProgressFor(task.Id) })
            .Where(value => value.Task.Enabled)
            .Where(value => value.Task.IsRecurring || !value.Progress.Completed)
            .Where(value => value.Progress.NextEligibleAtUtc is null || value.Progress.NextEligibleAtUtc <= now)
            .ToArray();

        // Cycling Expedition tasks rotate among themselves by least-recently-run (never = first),
        // so several Expedition presets cycle A→B→C→A until the plan is stopped, independent of
        // their row priority. The cycling group as a whole is anchored at its best (lowest)
        // priority so it still interleaves correctly with non-cycling tasks.
        var cycling = eligible
            .Where(value => value.Task.Kind == MacroTaskKind.Expedition && value.Task.Cycle)
            .ToArray();
        if (cycling.Length > 0)
        {
            var next = cycling
                .OrderBy(value => value.Progress.LastAttemptAt ?? DateTimeOffset.MinValue)
                .ThenBy(value => value.Task.Priority)
                .ThenBy(value => value.Index)
                .First();
            int anchor = cycling.Min(value => value.Task.Priority);
            var competitor = eligible
                .Where(value => !(value.Task.Kind == MacroTaskKind.Expedition && value.Task.Cycle))
                .OrderBy(value => value.Task.Priority)
                .ThenBy(value => value.Index)
                .FirstOrDefault();
            if (competitor is null || anchor <= competitor.Task.Priority) return next.Task;
            return competitor.Task;
        }

        return eligible
            .OrderBy(value => value.Task.Priority)
            .ThenBy(value => value.Index)
            .Select(value => value.Task)
            .FirstOrDefault();
    }

    internal static MacroTaskProgress Advance(
        MacroTaskDefinition task,
        MacroTaskProgress before,
        ScheduledTaskResult result,
        DateTimeOffset completedAt)
    {
        int victories = before.Victories + Math.Max(0, result.Victories);
        int defeats = before.Defeats + Math.Max(0, result.Defeats);
        long runtime = before.RuntimeSeconds + Math.Max(0, (long)Math.Ceiling(result.Runtime.TotalSeconds));
        bool completed = task.Kind switch
        {
            MacroTaskKind.Challenge => false,
            // A cycling Expedition never completes; it stays eligible so the scheduler keeps
            // rotating through the enabled Expedition presets until the plan is stopped.
            MacroTaskKind.Expedition when task.Cycle => false,
            MacroTaskKind.Story when task.CompleteOnRuntimeDefeat =>
                result.Defeats > 0 && runtime >= task.TargetRuntimeMinutes * 60L,
            _ => victories >= task.TargetVictories,
        };
        return before with
        {
            Victories = victories,
            Defeats = defeats,
            RuntimeSeconds = runtime,
            Completed = completed,
            LastAttemptAt = completedAt,
            LastCompletedAt = completed ? completedAt : before.LastCompletedAt,
            // Any task can become temporarily ineligible. Challenges use this for
            // their global reset, while finite modes use it after a safe skip so a
            // higher-priority camera/model problem cannot spin forever and starve
            // the rest of the plan.
            NextEligibleAtUtc = completed ? null : result.NextEligibleAtUtc,
        };
    }

    internal static bool CanRepeatStage(
        MacroTaskDefinition current,
        MacroTaskDefinition? following,
        ScheduledTaskResult result) =>
        !result.Skipped &&
        following is not null &&
        current.Kind is MacroTaskKind.Expedition or MacroTaskKind.Story or MacroTaskKind.Raid &&
        following.Kind == current.Kind &&
        string.Equals(following.PresetId, current.PresetId, StringComparison.OrdinalIgnoreCase);

    private static MacroPlan NormalizeProgress(MacroPlan plan) => plan with
    {
        Progress = plan.Tasks.Select(task => plan.ProgressFor(task.Id)).ToArray(),
    };

    private static DateTimeOffset? NextWake(MacroPlan plan, DateTimeOffset now) =>
        plan.Tasks
            .Where(task => task.Enabled && (task.IsRecurring || !plan.ProgressFor(task.Id).Completed))
            .Select(task => plan.ProgressFor(task.Id).NextEligibleAtUtc)
            .Where(value => value > now)
            .OrderBy(value => value)
            .FirstOrDefault();

    private async Task SaveAsync(MacroPlan plan, Action<MacroPlan>? changed, CancellationToken cancellationToken)
    {
        await _plans.SaveAsync(plan, cancellationToken).ConfigureAwait(false);
        changed?.Invoke(plan);
    }

    private static void LogResult(
        MacroTaskDefinition task,
        ScheduledTaskResult result,
        Action<MacroEvent>? log)
    {
        string outcome = result.Skipped
            ? "skipped"
            : result.Victories > 0
                ? $"recorded {result.Victories} victory"
                : result.Defeats > 0
                    ? $"recorded {result.Defeats} defeat"
                    : "updated eligibility";
        log?.Invoke(new MacroEvent(DateTimeOffset.Now, MacroEventLevel.Information, $"{DisplayName(task)} {outcome}.", "scheduler_progress"));
    }

    private static string DisplayName(MacroTaskDefinition task) =>
        string.IsNullOrWhiteSpace(task.Name) ? $"{task.Kind} preset {task.PresetId}" : task.Name;
}
