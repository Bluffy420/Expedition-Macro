namespace ExpeditionsMacro.Core.Models;

public enum MacroTaskKind
{
    Challenge,
    Expedition,
    Story,
    Raid,
}

public sealed record MacroTaskDefinition
{
    public required string Id { get; init; }
    public required MacroTaskKind Kind { get; init; }
    public required string PresetId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Priority { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public int TargetVictories { get; init; } = 1;
    public int TargetRuntimeMinutes { get; init; } = 180;
    public bool CompleteOnRuntimeDefeat { get; init; }

    // A cycling Expedition runs one full run, then yields to the next eligible preset so
    // several Expedition presets rotate (each map's loot differs) until the plan is stopped.
    public bool Cycle { get; init; }

    public bool IsRecurring => Kind == MacroTaskKind.Challenge || (Kind == MacroTaskKind.Expedition && Cycle);

    public void Validate()
    {
        ValidateId(Id, "task");
        ValidateId(PresetId, "preset");
        if (!Enum.IsDefined(Kind)) throw new InvalidDataException("Task type is invalid.");
        if (Priority is < 1 or > 9999) throw new InvalidDataException("Task priority must be 1 through 9999.");
        if (TargetVictories is < 1 or > 100000) throw new InvalidDataException("Victory target must be 1 through 100000.");
        if (CompleteOnRuntimeDefeat && Kind != MacroTaskKind.Story) throw new InvalidDataException("Only an Infinite Story task can use a runtime target.");
        if (Cycle && Kind != MacroTaskKind.Expedition) throw new InvalidDataException("Only an Expedition task can cycle through presets.");
        if (TargetRuntimeMinutes is < 1 or > 10080) throw new InvalidDataException("Runtime target must be 1 minute through 7 days.");
    }

    private static void ValidateId(string id, string label)
    {
        string name = Path.GetFileName(id);
        if (string.IsNullOrWhiteSpace(name) || name != id || id is "." or "..") throw new InvalidDataException($"The {label} id is invalid.");
    }
}

public sealed record MacroTaskProgress
{
    public required string TaskId { get; init; }
    public int Victories { get; init; }
    public int Defeats { get; init; }
    public long RuntimeSeconds { get; init; }
    public bool Completed { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastCompletedAt { get; init; }
    public DateTimeOffset? NextEligibleAtUtc { get; init; }
}

public sealed record MacroPlan
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<MacroTaskDefinition> Tasks { get; init; }
    public IReadOnlyList<MacroTaskProgress> Progress { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported macro plan format.");
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Macro plan identity is missing.");
        if (Tasks.Count == 0) throw new InvalidDataException("Add at least one task to the macro plan.");
        foreach (MacroTaskDefinition task in Tasks) task.Validate();
        if (Tasks.Select(task => task.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Tasks.Count)
        {
            throw new InvalidDataException("Every macro task must have a unique id.");
        }
        string[] taskIds = Tasks.Select(task => task.Id).ToArray();
        if (Progress.Select(value => value.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Progress.Count)
        {
            throw new InvalidDataException("Every macro task may have only one progress record.");
        }
        foreach (MacroTaskProgress value in Progress)
        {
            if (!taskIds.Contains(value.TaskId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Macro progress refers to a task that is no longer in the plan.");
            }
            if (value.Victories < 0 || value.Defeats < 0 || value.RuntimeSeconds < 0)
            {
                throw new InvalidDataException("Macro task progress cannot be negative.");
            }
        }
    }

    public MacroTaskProgress ProgressFor(string taskId) =>
        Progress.FirstOrDefault(value => string.Equals(value.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
        ?? new MacroTaskProgress { TaskId = taskId };

    public MacroPlan ResetProgress() => this with
    {
        Progress = Tasks.Select(task => new MacroTaskProgress { TaskId = task.Id }).ToArray(),
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
