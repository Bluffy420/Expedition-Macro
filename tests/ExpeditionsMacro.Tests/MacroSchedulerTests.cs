using ExpeditionsMacro.Automation.Scheduling;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;

namespace ExpeditionsMacro.Tests;

public sealed class MacroSchedulerTests
{
    [Fact]
    public void Selection_UsesTheFirstEligiblePriorityWithoutStarvingLowerTasksDuringCooldown()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MacroTaskDefinition high = Task("high", MacroTaskKind.Challenge, priority: 1);
        MacroTaskDefinition middle = Task("middle", MacroTaskKind.Story, priority: 2);
        MacroTaskDefinition low = Task("low", MacroTaskKind.Raid, priority: 3);
        MacroPlan plan = Plan(high, middle, low) with
        {
            Progress =
            [
                new MacroTaskProgress { TaskId = high.Id, NextEligibleAtUtc = now.AddMinutes(20) },
                new MacroTaskProgress { TaskId = middle.Id },
                new MacroTaskProgress { TaskId = low.Id },
            ],
        };

        Assert.Equal(middle, MacroScheduler.SelectNext(plan, now));
        Assert.Equal(high, MacroScheduler.SelectNext(plan, now.AddMinutes(21)));
    }

    [Fact]
    public void ChallengeRemainsTheOwnerBetweenMatches_ButCooldownHandsOffToTheNextMode()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MacroTaskDefinition challenge = Task("challenge", MacroTaskKind.Challenge, priority: 1);
        MacroTaskDefinition expedition = Task("expedition", MacroTaskKind.Expedition, priority: 2);
        MacroPlan plan = Plan(challenge, expedition);

        MacroTaskProgress afterMatch = MacroScheduler.Advance(
            challenge,
            plan.ProgressFor(challenge.Id),
            new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(4)),
            now);
        MacroPlan betweenMatches = plan with { Progress = [afterMatch, plan.ProgressFor(expedition.Id)] };

        Assert.Equal(challenge, MacroScheduler.SelectNext(betweenMatches, now));

        DateTimeOffset nextReset = now.AddMinutes(20);
        MacroTaskProgress onCooldown = MacroScheduler.Advance(
            challenge,
            afterMatch,
            new ScheduledTaskResult(0, 0, TimeSpan.FromSeconds(2), nextReset, Skipped: true),
            now.AddSeconds(2));
        MacroPlan readyForHandoff = plan with { Progress = [onCooldown, plan.ProgressFor(expedition.Id)] };

        Assert.Equal(expedition, MacroScheduler.SelectNext(readyForHandoff, now.AddSeconds(3)));
        Assert.Equal(challenge, MacroScheduler.SelectNext(readyForHandoff, nextReset));
    }

    [Fact]
    public void Selection_SkipsCompletedAndDisabledFiniteTasks_ButChallengeRemainsRecurring()
    {
        MacroTaskDefinition challenge = Task("challenge", MacroTaskKind.Challenge, priority: 3);
        MacroTaskDefinition complete = Task("complete", MacroTaskKind.Expedition, priority: 1);
        MacroTaskDefinition disabled = Task("disabled", MacroTaskKind.Raid, priority: 2) with { Enabled = false };
        MacroPlan plan = Plan(complete, disabled, challenge) with
        {
            Progress =
            [
                new MacroTaskProgress { TaskId = complete.Id, Completed = true },
                new MacroTaskProgress { TaskId = disabled.Id },
                new MacroTaskProgress { TaskId = challenge.Id, Completed = true },
            ],
        };

        Assert.Equal(challenge, MacroScheduler.SelectNext(plan, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FiniteTask_CompletesAtItsVictoryTarget()
    {
        MacroTaskDefinition task = Task("story", MacroTaskKind.Story, priority: 1) with { TargetVictories = 2 };
        MacroTaskProgress before = new() { TaskId = task.Id, Victories = 1 };

        MacroTaskProgress after = MacroScheduler.Advance(
            task,
            before,
            new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(3)),
            DateTimeOffset.UtcNow);

        Assert.True(after.Completed);
        Assert.Equal(2, after.Victories);
        Assert.Equal(180, after.RuntimeSeconds);
        Assert.NotNull(after.LastCompletedAt);
    }

    [Fact]
    public void InfiniteStory_CompletesOnlyOnDefeatAfterItsRuntimeTarget()
    {
        MacroTaskDefinition task = Task("infinite", MacroTaskKind.Story, priority: 1) with
        {
            CompleteOnRuntimeDefeat = true,
            TargetRuntimeMinutes = 60,
        };
        MacroTaskProgress nearlyDone = new() { TaskId = task.Id, RuntimeSeconds = 3590 };

        MacroTaskProgress victory = MacroScheduler.Advance(
            task,
            nearlyDone,
            new ScheduledTaskResult(1, 0, TimeSpan.FromSeconds(20)),
            DateTimeOffset.UtcNow);
        MacroTaskProgress defeat = MacroScheduler.Advance(
            task,
            nearlyDone,
            new ScheduledTaskResult(0, 1, TimeSpan.FromSeconds(20)),
            DateTimeOffset.UtcNow);

        Assert.False(victory.Completed);
        Assert.True(defeat.Completed);
    }

    [Fact]
    public void SafeSkip_TemporarilyDefersAFiniteTask()
    {
        DateTimeOffset retry = DateTimeOffset.UtcNow.AddMinutes(5);
        MacroTaskDefinition task = Task("raid", MacroTaskKind.Raid, priority: 1);

        MacroTaskProgress after = MacroScheduler.Advance(
            task,
            new MacroTaskProgress { TaskId = task.Id },
            new ScheduledTaskResult(0, 0, TimeSpan.Zero, retry, Skipped: true),
            DateTimeOffset.UtcNow);

        Assert.False(after.Completed);
        Assert.Equal(retry, after.NextEligibleAtUtc);
    }

    [Theory]
    [InlineData(MacroTaskKind.Expedition, true)]
    [InlineData(MacroTaskKind.Story, true)]
    [InlineData(MacroTaskKind.Raid, true)]
    [InlineData(MacroTaskKind.Challenge, false)]
    public void RepeatStage_RequiresTheSameFiniteRoute(MacroTaskKind kind, bool expected)
    {
        MacroTaskDefinition current = Task("current", kind, 1) with { PresetId = "same-route" };
        MacroTaskDefinition following = Task("following", kind, 2) with { PresetId = "same-route" };

        Assert.Equal(expected, MacroScheduler.CanRepeatStage(
            current,
            following,
            new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(2))));
        Assert.False(MacroScheduler.CanRepeatStage(
            current,
            following with { PresetId = "different-route" },
            new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(2))));
        Assert.False(MacroScheduler.CanRepeatStage(
            current,
            following,
            new ScheduledTaskResult(0, 0, TimeSpan.Zero, Skipped: true)));
    }

    [Fact]
    public async Task MatchCallback_PersistsEachRepeatedStageBeforeTheRunnerContinues()
    {
        string root = TestPaths.NewTemporaryDirectory();
        try
        {
            AppPaths paths = new(root);
            MacroPlanRepository repository = new(paths);
            MacroScheduler scheduler = new(repository);
            MacroTaskDefinition expedition = Task("expedition", MacroTaskKind.Expedition, 1) with { TargetVictories = 2 };
            MacroPlan plan = Plan(expedition);
            using CancellationTokenSource stopped = new();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(
                plan,
                async (_, record, token) =>
                {
                    ScheduledTaskContinuation first = await record(
                        new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(3)),
                        token);
                    Assert.Equal(ScheduledTaskContinuation.RepeatStage, first);
                    MacroPlan afterFirst = Assert.IsType<MacroPlan>(await repository.LoadAsync(plan.Id, token));
                    Assert.Equal(1, afterFirst.ProgressFor(expedition.Id).Victories);

                    ScheduledTaskContinuation second = await record(
                        new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(3)),
                        token);
                    Assert.Equal(ScheduledTaskContinuation.Handoff, second);
                    stopped.Cancel();
                    return new ScheduledTaskResult(2, 0, TimeSpan.FromMinutes(6));
                },
                cancellationToken: stopped.Token));

            MacroPlan completed = Assert.IsType<MacroPlan>(await repository.LoadAsync(plan.Id));
            Assert.Equal(2, completed.ProgressFor(expedition.Id).Victories);
            Assert.True(completed.ProgressFor(expedition.Id).Completed);
        }
        finally
        {
            TestPaths.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ResetProgress_PersistsAnEmptyRecordForEveryTask()
    {
        string root = TestPaths.NewTemporaryDirectory();
        try
        {
            AppPaths paths = new(root);
            MacroPlanRepository repository = new(paths);
            MacroScheduler scheduler = new(repository);
            MacroTaskDefinition first = Task("first", MacroTaskKind.Expedition, 1);
            MacroTaskDefinition second = Task("second", MacroTaskKind.Raid, 2);
            MacroPlan plan = Plan(first, second) with
            {
                Progress =
                [
                    new MacroTaskProgress { TaskId = first.Id, Victories = 4, Completed = true },
                    new MacroTaskProgress { TaskId = second.Id, Defeats = 2 },
                ],
            };

            MacroPlan reset = await scheduler.ResetProgressAsync(plan);
            MacroPlan loaded = Assert.IsType<MacroPlan>(await repository.LoadAsync(plan.Id));

            Assert.All(reset.Progress, value =>
            {
                Assert.Equal(0, value.Victories);
                Assert.Equal(0, value.Defeats);
                Assert.False(value.Completed);
            });
            Assert.Equal(reset.Progress, loaded.Progress);
        }
        finally
        {
            TestPaths.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void CyclingExpedition_IsRecurringAndNeverCompletes()
    {
        MacroTaskDefinition cycle = Task("cycle", MacroTaskKind.Expedition, priority: 1) with { Cycle = true, TargetVictories = 1 };
        Assert.True(cycle.IsRecurring);
        cycle.Validate();

        MacroTaskProgress after = MacroScheduler.Advance(
            cycle,
            new MacroTaskProgress { TaskId = cycle.Id },
            new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(3)),
            DateTimeOffset.UtcNow);

        Assert.False(after.Completed);
        Assert.Equal(1, after.Victories);
    }

    [Fact]
    public void Cycle_IsRejectedForNonExpeditionTasks()
    {
        MacroTaskDefinition challenge = Task("challenge", MacroTaskKind.Challenge, priority: 1) with { Cycle = true };
        MacroTaskDefinition story = Task("story", MacroTaskKind.Story, priority: 1) with { Cycle = true };

        Assert.Throws<InvalidDataException>(challenge.Validate);
        Assert.Throws<InvalidDataException>(story.Validate);
    }

    [Fact]
    public void CyclingExpeditions_RoundRobinByLeastRecentlyRun()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MacroTaskDefinition a = Task("a", MacroTaskKind.Expedition, priority: 1) with { Cycle = true };
        MacroTaskDefinition b = Task("b", MacroTaskKind.Expedition, priority: 2) with { Cycle = true };
        MacroTaskDefinition c = Task("c", MacroTaskKind.Expedition, priority: 3) with { Cycle = true };
        MacroPlan plan = Plan(a, b, c);

        // Nothing has run yet: the never-run tasks rotate in priority order starting with A.
        Assert.Equal(a, MacroScheduler.SelectNext(plan, now));

        // After A runs, B (never run) is next even though A has higher priority.
        MacroTaskProgress afterA = MacroScheduler.Advance(a, plan.ProgressFor(a.Id), new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(2)), now);
        plan = plan with { Progress = [afterA, plan.ProgressFor(b.Id), plan.ProgressFor(c.Id)] };
        Assert.Equal(b, MacroScheduler.SelectNext(plan, now.AddSeconds(1)));

        // After B runs, C (never run) is next.
        MacroTaskProgress afterB = MacroScheduler.Advance(b, plan.ProgressFor(b.Id), new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(2)), now.AddSeconds(1));
        plan = plan with { Progress = [afterA, afterB, plan.ProgressFor(c.Id)] };
        Assert.Equal(c, MacroScheduler.SelectNext(plan, now.AddSeconds(2)));

        // After C runs, all have run; the least-recently-run (A) rotates back to the front.
        MacroTaskProgress afterC = MacroScheduler.Advance(c, plan.ProgressFor(c.Id), new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(2)), now.AddSeconds(2));
        plan = plan with { Progress = [afterA, afterB, afterC] };
        Assert.Equal(a, MacroScheduler.SelectNext(plan, now.AddSeconds(3)));
    }

    [Fact]
    public void CyclingExpeditions_StayEligibleAcrossManyRuns()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MacroTaskDefinition a = Task("a", MacroTaskKind.Expedition, priority: 1) with { Cycle = true };
        MacroTaskDefinition b = Task("b", MacroTaskKind.Expedition, priority: 2) with { Cycle = true };
        MacroPlan plan = Plan(a, b);

        // Simulate several cycles; selection must keep alternating and never return null.
        string[] expected = ["a", "b", "a", "b", "a", "b"];
        foreach (string expectedId in expected)
        {
            MacroTaskDefinition? next = MacroScheduler.SelectNext(plan, now);
            Assert.NotNull(next);
            Assert.Equal(expectedId, next.Id);
            MacroTaskProgress after = MacroScheduler.Advance(next, plan.ProgressFor(next.Id), new ScheduledTaskResult(1, 0, TimeSpan.FromMinutes(2)), now);
            plan = plan with { Progress = plan.Tasks.Select(task => task.Id == next.Id ? after : plan.ProgressFor(task.Id)).ToArray() };
            now = now.AddSeconds(1);
        }
    }

    private static MacroTaskDefinition Task(string id, MacroTaskKind kind, int priority) => new()
    {
        Id = id,
        Kind = kind,
        PresetId = $"{id}-preset",
        Name = id,
        Priority = priority,
    };

    private static MacroPlan Plan(params MacroTaskDefinition[] tasks) => new()
    {
        Id = "test-plan",
        Name = "Test plan",
        Tasks = tasks,
    };
}
