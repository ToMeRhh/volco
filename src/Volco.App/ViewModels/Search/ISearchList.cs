namespace Volco.App.ViewModels.Search;

/// <summary>A dialog list driven from its search box (spec controls-page §6.3): Down and Up move
/// through the results, Enter picks the current one.</summary>
public interface ISearchList
{
    void MoveSelection(int step);

    void PickCurrent();
}
