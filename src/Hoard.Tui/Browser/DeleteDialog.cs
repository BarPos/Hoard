using Hoard.Core.Actions;

namespace Hoard.Tui.Browser;

public enum DialogStage { Choose, Confirm, Running }

/// <summary>State of the delete confirmation (plan.md §8.1, mock: examples/13-delete-confirm.sh).</summary>
public sealed class DeleteDialog(DeletePlan plan, bool trashAvailable, string trashName)
{
    public const int TrashChoice = 0, PermanentChoice = 1, CancelChoice = 2;

    public DeletePlan Plan { get; } = plan;
    public bool TrashAvailable { get; } = trashAvailable;
    public string TrashName { get; } = trashName;
    public DialogStage Stage { get; set; } = DialogStage.Choose;
    /// <summary>Focused button: 0 Trash, 1 permanent, 2 cancel. Starts on the safe choice.</summary>
    public int Choice { get; set; } = plan.IsEmpty ? CancelChoice : trashAvailable ? TrashChoice : CancelChoice;
    public string Typed { get; set; } = "";
    public string? Hint { get; set; }
    public bool Permanent { get; set; }
    private int _done;
    public int Done => Volatile.Read(ref _done);
    internal void SetDone(int n) => Volatile.Write(ref _done, n);

    /// <summary>What must be typed to confirm a permanent delete: the name for one item, "delete" for several.</summary>
    public string ConfirmWord => Plan.Items.Count == 1 ? Path.GetFileName(Plan.Items[0].Path.TrimEnd('/')) : "delete";

    public bool IsEnabled(int choice) => choice switch
    {
        TrashChoice => TrashAvailable && !Plan.IsEmpty,
        PermanentChoice => !Plan.IsEmpty,
        _ => true,
    };

    public void Move(int delta)
    {
        int c = Choice;
        for (int i = 0; i < 3; i++)
        {
            c = (c + delta + 3) % 3;
            if (IsEnabled(c))
            {
                Choice = c;
                return;
            }
        }
    }
}
