using System.Windows.Automation;

namespace Glideslope.NativeProof;

/// <summary>Finds and invokes card buttons by accessible name through UI Automation. InvokePattern works
/// on a hidden desktop without cursor input. Callers wait for the app's startup restore or prior save signal
/// before querying the automation tree.</summary>
internal static class AutomationInterop
{
    /// <summary>Finds a descendant button by accessible name and invokes its InvokePattern. On failure,
    /// reports the accessible names present in the window.</summary>
    public static bool InvokeButtonByName(nint hwnd, string name, Action<string> log)
    {
        var root = TryGetRoot(hwnd, name, "automation_invoke_failed", log);
        if (root is null) return false;

        var button = FindDescendantByName(root, name);
        if (button is null)
        {
            log($"automation_invoke_failed name=\"{name}\" reason=element_not_found hwnd=0x{hwnd:X} found=[{string.Join(",", DescendantNames(root))}]");
            return false;
        }

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject) || patternObject is not InvokePattern invoke)
        {
            log($"automation_invoke_failed name=\"{name}\" reason=invoke_pattern_unavailable hwnd=0x{hwnd:X}");
            return false;
        }

        invoke.Invoke();
        log($"automation_invoke_succeeded name=\"{name}\" hwnd=0x{hwnd:X}");
        return true;
    }

    /// <summary>Checks whether a named button is present without invoking it. Mode scenarios use the
    /// toggle button's accessible name to identify the card's current mode.</summary>
    public static bool ButtonExists(nint hwnd, string name, Action<string> log)
    {
        var root = TryGetRoot(hwnd, name, "automation_probe_failed", log);
        if (root is null) return false;

        if (FindDescendantByName(root, name) is not null)
        {
            log($"automation_probe_found name=\"{name}\" hwnd=0x{hwnd:X}");
            return true;
        }

        log($"automation_probe_not_found name=\"{name}\" hwnd=0x{hwnd:X} found=[{string.Join(",", DescendantNames(root))}]");
        return false;
    }

    /// <summary>Finds a named descendant in the control view, then the raw view. Hidden Avalonia controls
    /// may have no automation peer, so callers should target controls available in the current mode.</summary>
    private static AutomationElement? FindDescendantByName(AutomationElement root, string name)
    {
        var nameCondition = new PropertyCondition(AutomationElement.NameProperty, name);
        var controlViewMatch = root.FindFirst(TreeScope.Descendants, nameCondition);
        if (controlViewMatch is not null) return controlViewMatch;

        return root.FindFirst(TreeScope.Subtree, new AndCondition(Automation.RawViewCondition, nameCondition));
    }

    /// <summary>Returns non-empty accessible names among the root's descendants. If the window disappears
    /// during enumeration, retains names already collected for failure evidence.</summary>
    private static IReadOnlyList<string> DescendantNames(AutomationElement root)
    {
        var names = new List<string>();
        try
        {
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var name = element.Current.Name;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        catch (ElementNotAvailableException)
        {
            // Reported as evidence either way; a partial list beats none.
        }

        return names;
    }

    private static AutomationElement? TryGetRoot(nint hwnd, string name, string failureCode, Action<string> log)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is not null) return root;
        }
        catch (ElementNotAvailableException)
        {
            // Reported below as root_element_unavailable, the same failure a null FromHandle result gets.
        }

        log($"{failureCode} name=\"{name}\" reason=root_element_unavailable hwnd=0x{hwnd:X}");
        return null;
    }
}
