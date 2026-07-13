namespace LocalTranslator.Core.Models;

public readonly record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 1 && Height > 1;
}

