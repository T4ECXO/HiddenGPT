using HiddenGPT;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}

var input = new SideButtonShortcut();
Check(!input.Process(0x80), "An orphan release must not toggle the mode.");
Check(!input.Process(0x40), "Holding Mouse 4 must not repeatedly toggle.");
Check(!input.Process(0), "Mouse motion while held must not toggle.");
Check(!input.Process(0x100), "The other side button must not toggle.");
Check(!input.Process(0x200), "Releasing the other side button must not toggle.");
Check(input.Process(0x80), "Releasing Mouse 4 must toggle once.");
Check(!input.Process(0x80), "A duplicate release must not undo the toggle.");
Check(!input.Process(0x40), "The next press starts a fresh gesture.");
Check(input.Process(0x80), "The second gesture must return to interactive mode.");
input.Process(0x40);
input.SelectButton(5);
Check(!input.Process(0x200), "Changing the binding must clear held-button state.");
Check(!input.Process(0xC0), "The old binding must stop toggling.");
Check(input.Process(0x300), "Combined Mouse 5 down/up flags must toggle once.");
Check(!input.Process(0x200), "Mouse 5 duplicate release must not toggle.");
Check(SideButtonShortcut.ReleasedButton(0x80) == 4, "Learn Mouse 4.");
Check(SideButtonShortcut.ReleasedButton(0x200) == 5, "Learn Mouse 5.");
Check(SideButtonShortcut.ReleasedButton(0x400) == 0, "Scrolling must not select a side button.");
Console.WriteLine("PASS: 16 side-button gesture and rebinding checks.");
