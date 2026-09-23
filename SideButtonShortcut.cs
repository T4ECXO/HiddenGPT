namespace HiddenGPT;

// Raw Input flags are independent of focus, hit testing, and the virtual cursor.
internal sealed class SideButtonShortcut
{
    public int Button { get; private set; } = 4;
    private bool _pressed;

    public void SelectButton(int button)
    {
        if (button is not (4 or 5)) throw new ArgumentOutOfRangeException(nameof(button));
        Button = button;
        _pressed = false;
    }

    public static int ReleasedButton(ushort flags) =>
        (flags & 0x0080) != 0 ? 4 : (flags & 0x0200) != 0 ? 5 : 0;

    public bool Process(ushort flags)
    {
        var down = Button == 4 ? 0x0040 : 0x0100;
        var up = Button == 4 ? 0x0080 : 0x0200;
        if ((flags & down) != 0) _pressed = true;
        if ((flags & up) == 0) return false;
        var toggle = _pressed;
        _pressed = false;
        return toggle;
    }
}
