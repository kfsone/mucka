#pragma warning disable CA1711 // AppDelegate is the name Info.plist and the platform entry point use
#pragma warning disable CA1822 // the Objective-C selector for a key command must be an instance method

using Foundation;
using UIKit;

namespace Mucka;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override void BuildMenu(IUIMenuBuilder builder)
	{
		base.BuildMenu(builder);
		if (builder.System == UIMenuSystem.MainSystem)
			builder.InsertSiblingMenuAfter(MacHotkeys.Build(), UIMenuIdentifier.Edit.GetConstant()!);
	}

	public override void ValidateCommand(UICommand command)
	{
		base.ValidateCommand(command);
		if (command is UIKeyCommand key && key.Action?.Name == MacHotkeys.Action)
			key.Attributes = MacHotkeys.IsActive() ? 0 : UIMenuElementAttributes.Disabled;
	}

	// Copy walks the responder chain from the command box: the text field takes it when it has a
	// selection, and declines otherwise, which lets it arrive here.
	public override void Copy(NSObject? sender)
	{
		if (MacHotkeys.CanCopyTerminal?.Invoke() == true)
			MacHotkeys.CopyTerminal?.Invoke();
		else
			base.Copy(sender);
	}

	public override bool CanPerform(ObjCRuntime.Selector action, NSObject? withSender)
		=> (action.Name == "copy:" && MacHotkeys.CanCopyTerminal?.Invoke() == true)
			|| base.CanPerform(action, withSender);

	[Export(MacHotkeys.Action)]
	public void OnHotkey(UIKeyCommand command) => MacHotkeys.Fire(command);

	[Export(MacEscape.Action)]
	public void OnEscapePage(UIKeyCommand command) => MacEscape.Fire();
}
