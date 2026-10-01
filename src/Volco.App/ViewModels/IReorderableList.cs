namespace Volco.App.ViewModels;

/// <summary>
/// A card list whose order is meaningful: position is the hardware input number, so moving a
/// card moves its whole configuration to another physical control. Driven by
/// <see cref="Views.CardReorder"/> (drag and Alt+Up/Down).
/// </summary>
public interface IReorderableList
{
    int Count { get; }

    /// <summary>Relabels the cards as if the card at <paramref name="from"/> were dropped at
    /// <paramref name="to"/>, without changing anything. Used while dragging.</summary>
    void PreviewMove(int from, int to);

    /// <summary>Undoes <see cref="PreviewMove"/>.</summary>
    void CancelPreview();

    /// <summary>Moves the card (and its configuration) and saves.</summary>
    void Move(int from, int to);
}
