namespace DesktopRelay.App;
public sealed class CapsuleAnchor
{
    private (double Left, double Top)? position;
    public void Remember(double left, double top) => position = (left, top);
    public (double Left, double Top) Restore(double left, double top) => position ?? (left, top);
}
