namespace OptiScalerInstaller.App.ViewModels;

public sealed class GameFilterOptionViewModel
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public override string ToString() => Label;
}
