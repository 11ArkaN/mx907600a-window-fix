using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

internal static class Program
{
    private const string TargetProcessName = "MX907600A";
    private const string TargetWindowClass = "ThunderRT6FormDC";
    private const string TargetWindowTitle = "MX907600A";
    private const string TargetExeFileName = "MX907600A.exe";
    private const string TargetDrawProxyFileName = "Draw9076.dll";
    private const string TargetDrawRealFileName = "Draw9076.real.dll";
    private const string DrawProxyResourceName = "MX907600AWindowFix.Draw9076.dll";
    private const string DrawRealResourceName = "MX907600AWindowFix.Draw9076.real.dll";

    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_THICKFRAME = 0x00040000;

    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_ENABLED = 0x00000000;
    private const uint SC_SIZE = 0xF000;
    private const uint SC_MAXIMIZE = 0xF030;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const int WM_GETFONT = 0x0031;
    private const int WM_SETFONT = 0x0030;

    private static readonly WinEventProc MarkerMoveWinEventCallback = OnMarkerMoveWinEvent;
    private static IntPtr MarkerMoveHook = IntPtr.Zero;
    private static IntPtr MarkerMoveRoot = IntPtr.Zero;
    private static int MarkerMoveProcessId;
    private static bool MarkerMoveFixInProgress;
    private static readonly string LauncherPath = Assembly.GetExecutingAssembly().Location;
    private static readonly string LauncherDirectory = Path.GetDirectoryName(LauncherPath);

    private static int Main(string[] args)
    {
        if (!EnsureElevated(args))
        {
            return 0;
        }

        string targetExePath = GetTargetExePath();
        if (targetExePath == null)
        {
            return 2;
        }

        EnsureBundledFilesInstalled();

        bool hasArguments = args != null && args.Length > 0;
        int existingPid = FindExistingProcessId();
        if (!hasArguments && existingPid != 0)
        {
            return AttachAndScale(existingPid);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = targetExePath,
            WorkingDirectory = Path.GetDirectoryName(targetExePath),
            Arguments = BuildArguments(args),
            UseShellExecute = false
        };

        using (Process process = Process.Start(startInfo))
        {
            if (process == null)
            {
                return 3;
            }

            if (!hasArguments)
            {
                return AttachAndScale(process.Id);
            }

            Thread.Sleep(500);

            int targetPid = process.HasExited ? FindExistingProcessId() : process.Id;
            if (targetPid == 0)
            {
                targetPid = process.Id;
            }

            return AttachAndScale(targetPid);
        }
    }

    private static bool EnsureElevated(string[] args)
    {
        if (IsRunningAsAdministrator())
        {
            return true;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = LauncherPath,
                WorkingDirectory = LauncherDirectory,
                Arguments = BuildArguments(args),
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(startInfo);
        }
        catch
        {
        }

        return false;
    }

    private static bool IsRunningAsAdministrator()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            if (identity == null)
            {
                return false;
            }

            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static string GetTargetExePath()
    {
        string directPath = Path.Combine(LauncherDirectory, TargetExeFileName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return null;
    }

    private static void EnsureBundledFilesInstalled()
    {
        if (!Directory.Exists(LauncherDirectory))
        {
            return;
        }

        WriteEmbeddedFileIfDifferent(DrawProxyResourceName, Path.Combine(LauncherDirectory, TargetDrawProxyFileName));
        WriteEmbeddedFileIfDifferent(DrawRealResourceName, Path.Combine(LauncherDirectory, TargetDrawRealFileName));
    }

    private static void WriteEmbeddedFileIfDifferent(string resourceName, string destinationPath)
    {
        byte[] payload = ReadEmbeddedResource(resourceName);
        if (payload == null || payload.Length == 0)
        {
            return;
        }

        try
        {
            if (File.Exists(destinationPath))
            {
                byte[] existing = File.ReadAllBytes(destinationPath);
                if (existing.Length == payload.Length)
                {
                    bool same = true;
                    for (int i = 0; i < existing.Length; i++)
                    {
                        if (existing[i] != payload[i])
                        {
                            same = false;
                            break;
                        }
                    }

                    if (same)
                    {
                        return;
                    }
                }
            }

            File.WriteAllBytes(destinationPath, payload);
        }
        catch
        {
        }
    }

    private static byte[] ReadEmbeddedResource(string resourceName)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using (Stream stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
            {
                return null;
            }

            byte[] buffer = new byte[stream.Length];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset == buffer.Length)
            {
                return buffer;
            }

            byte[] trimmed = new byte[offset];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, offset);
            return trimmed;
        }
    }

    private static int AttachAndScale(int processId)
    {
        IntPtr root = WaitForTargetWindow(processId, TimeSpan.FromSeconds(20));
        if (root == IntPtr.Zero)
        {
            return 4;
        }

        if (IsZoomed(root))
        {
            ShowWindow(root, SW_RESTORE);
            Thread.Sleep(200);
        }

        LayoutSnapshot snapshot = CaptureLayout(root);
        if (snapshot.Controls.Count == 0 || snapshot.RootClientWidth <= 0 || snapshot.RootClientHeight <= 0)
        {
            PatchFrame(root);
            ShowWindow(root, SW_MAXIMIZE);
            WaitForProcessExit(processId);
            return 0;
        }

        PatchFrame(root);
        ShowWindow(root, SW_RESTORE);
        Thread.Sleep(80);
        ShowWindow(root, SW_MAXIMIZE);
        Thread.Sleep(250);

        InstallMarkerMoveHook(processId, root);
        MonitorAndScale(processId, root, snapshot);
        UninstallMarkerMoveHook();
        return 0;
    }

    private static int FindExistingProcessId()
    {
        Process[] processes = Process.GetProcessesByName(TargetProcessName);
        if (processes.Length == 0)
        {
            return 0;
        }

        return processes[0].Id;
    }

    private static void MonitorAndScale(int processId, IntPtr root, LayoutSnapshot snapshot)
    {
        int lastWidth = -1;
        int lastHeight = -1;

        while (IsProcessAlive(processId) && IsWindow(root))
        {
            PatchFrame(root);
            if (!IsZoomed(root))
            {
                ShowWindow(root, SW_MAXIMIZE);
                Thread.Sleep(80);
            }

            bool addedControls = DiscoverVisibleControls(root, snapshot);

            int width;
            int height;
            GetClientSize(root, out width, out height);

            if (addedControls || width != lastWidth || height != lastHeight)
            {
                ApplyLayout(root, snapshot);
                lastWidth = width;
                lastHeight = height;
            }
            else
            {
                ApplyDynamicAnchors(root, snapshot);
            }

            FixMarkerMovePanel(root);

            Thread.Sleep(120);
        }

        snapshot.DisposeFonts();
    }

    private static LayoutSnapshot CaptureLayout(IntPtr root)
    {
        var snapshot = new LayoutSnapshot();
        snapshot.Root = root;
        GetClientSize(root, out snapshot.RootClientWidth, out snapshot.RootClientHeight);
        snapshot.BaseClientSizes[root] = new SizePair(snapshot.RootClientWidth, snapshot.RootClientHeight);

        EnumChildWindows(
            root,
            delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                IntPtr parent = GetParent(hwnd);
                if (parent == IntPtr.Zero)
                {
                    return true;
                }

                if (!snapshot.BaseClientSizes.ContainsKey(parent))
                {
                    int parentWidth;
                    int parentHeight;
                    GetClientSize(parent, out parentWidth, out parentHeight);
                    snapshot.BaseClientSizes[parent] = new SizePair(parentWidth, parentHeight);
                }

                SizePair parentClient = snapshot.BaseClientSizes[parent];
                if (parentClient.Width <= 0 || parentClient.Height <= 0)
                {
                    return true;
                }

                RECT rect = new RECT();
                GetWindowRect(hwnd, out rect);
                MapWindowPoints(IntPtr.Zero, parent, ref rect, 2);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                {
                    return true;
                }

                if (rect.Right <= 0 || rect.Bottom <= 0 || rect.Left >= parentClient.Width || rect.Top >= parentClient.Height)
                {
                    return true;
                }

                var captured = new CapturedControl
                {
                    Hwnd = hwnd,
                    Parent = parent,
                    Depth = GetDepth(hwnd, root),
                    BaseLeft = rect.Left,
                    BaseTop = rect.Top,
                    BaseWidth = width,
                    BaseHeight = height,
                    Font = ReadFont(hwnd),
                    ClassName = ReadClassName(hwnd),
                    WindowTitle = ReadWindowTitle(hwnd),
                    AnchorBesideTable = IsMarkerMovePanel(hwnd)
                };

                AssignBottomAnchorMetadata(captured);

                snapshot.Controls.Add(captured);
                snapshot.KnownControlHandles.Add(hwnd.ToInt64());

                int clientWidth;
                int clientHeight;
                GetClientSize(hwnd, out clientWidth, out clientHeight);
                snapshot.BaseClientSizes[hwnd] = new SizePair(clientWidth, clientHeight);
                return true;
            },
            IntPtr.Zero);

        snapshot.Controls.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        return snapshot;
    }

    private static bool DiscoverVisibleControls(IntPtr root, LayoutSnapshot snapshot)
    {
        bool addedAny = false;

        EnumChildWindows(
            root,
            delegate (IntPtr hwnd, IntPtr lParam)
            {
                long handle = hwnd.ToInt64();
                if (snapshot.KnownControlHandles.Contains(handle) || !IsWindowVisible(hwnd))
                {
                    return true;
                }

                IntPtr parent = GetParent(hwnd);
                if (parent == IntPtr.Zero)
                {
                    return true;
                }

                if (!snapshot.BaseClientSizes.ContainsKey(parent))
                {
                    int parentWidth;
                    int parentHeight;
                    GetClientSize(parent, out parentWidth, out parentHeight);
                    snapshot.BaseClientSizes[parent] = new SizePair(parentWidth, parentHeight);
                }

                SizePair baseParentClient = snapshot.BaseClientSizes[parent];
                if (baseParentClient.Width <= 0 || baseParentClient.Height <= 0)
                {
                    return true;
                }

                RECT rect = new RECT();
                GetWindowRect(hwnd, out rect);
                MapWindowPoints(IntPtr.Zero, parent, ref rect, 2);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                {
                    return true;
                }

                var captured = new CapturedControl
                {
                    Hwnd = hwnd,
                    Parent = parent,
                    Depth = GetDepth(hwnd, root),
                    BaseLeft = rect.Left,
                    BaseTop = rect.Top,
                    BaseWidth = width,
                    BaseHeight = height,
                    Font = ReadFont(hwnd),
                    ClassName = ReadClassName(hwnd),
                    WindowTitle = ReadWindowTitle(hwnd),
                    AnchorBesideTable = IsMarkerMovePanel(hwnd)
                };

                AssignBottomAnchorMetadata(captured);

                snapshot.Controls.Add(captured);
                snapshot.KnownControlHandles.Add(handle);

                int clientWidth;
                int clientHeight;
                GetClientSize(hwnd, out clientWidth, out clientHeight);
                snapshot.BaseClientSizes[hwnd] = new SizePair(clientWidth, clientHeight);
                addedAny = true;
                return true;
            },
            IntPtr.Zero);

        if (addedAny)
        {
            snapshot.Controls.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        }

        return addedAny;
    }

    private static void ApplyLayout(IntPtr root, LayoutSnapshot snapshot)
    {
        foreach (CapturedControl control in snapshot.Controls)
        {
            ApplyControlLayout(control, snapshot, root, true);
        }

        InvalidateRect(root, IntPtr.Zero, true);
        UpdateWindow(root);
    }

    private static void ApplyDynamicAnchors(IntPtr root, LayoutSnapshot snapshot)
    {
        foreach (CapturedControl control in snapshot.Controls)
        {
            if (!control.AnchorBesideTable && !control.AnchorBelowGraph)
            {
                continue;
            }

            ApplyControlLayout(control, snapshot, root, false);
        }
    }

    private static void ApplyControlLayout(CapturedControl control, LayoutSnapshot snapshot, IntPtr root, bool updateFont)
    {
        if (!IsWindow(control.Hwnd) || !IsWindow(control.Parent))
        {
            return;
        }

        SizePair baseParentClient;
        if (!snapshot.BaseClientSizes.TryGetValue(control.Parent, out baseParentClient))
        {
            return;
        }

        if (baseParentClient.Width <= 0 || baseParentClient.Height <= 0)
        {
            return;
        }

        int currentParentWidth;
        int currentParentHeight;
        GetClientSize(control.Parent, out currentParentWidth, out currentParentHeight);
        if (currentParentWidth <= 0 || currentParentHeight <= 0)
        {
            return;
        }

        double scaleX = (double)currentParentWidth / baseParentClient.Width;
        double scaleY = (double)currentParentHeight / baseParentClient.Height;

        int newLeft = (int)Math.Round(control.BaseLeft * scaleX);
        int newTop = (int)Math.Round(control.BaseTop * scaleY);
        int newWidth = Math.Max(1, (int)Math.Round(control.BaseWidth * scaleX));
        int newHeight = Math.Max(1, (int)Math.Round(control.BaseHeight * scaleY));

        if (control.AnchorBesideTable)
        {
            RECT? bottomContentRect = FindBottomContentRect(control.Parent, control.Hwnd);
            if (bottomContentRect.HasValue)
            {
                RECT anchorRect = bottomContentRect.Value;
                RECT? markerPositionRect = FindSiblingRect(control.Parent, control.Hwnd, "ThunderRT6Frame", "Marker Position");

                newLeft = anchorRect.Right + 16;
                newTop = anchorRect.Top;

                if (markerPositionRect.HasValue)
                {
                    int maxLeft = markerPositionRect.Value.Left - newWidth - 16;
                    if (newLeft > maxLeft)
                    {
                        newLeft = maxLeft;
                    }
                }
            }
        }

        if (control.AnchorBelowGraph)
        {
            RECT? graphRect = FindLargestGraphRect(control.Parent, control.Hwnd);
            if (graphRect.HasValue)
            {
                int minTop = graphRect.Value.Bottom + control.BaseGapBelowGraph;
                if (newTop < minTop)
                {
                    newTop = minTop;
                }
            }
        }

        RECT currentRect = new RECT();
        GetWindowRect(control.Hwnd, out currentRect);
        MapWindowPoints(IntPtr.Zero, control.Parent, ref currentRect, 2);

        int currentLeft = currentRect.Left;
        int currentTop = currentRect.Top;
        int currentWidth = currentRect.Right - currentRect.Left;
        int currentHeight = currentRect.Bottom - currentRect.Top;

        if (currentLeft == newLeft && currentTop == newTop && currentWidth == newWidth && currentHeight == newHeight)
        {
            return;
        }

        SetWindowPos(
            control.Hwnd,
            IntPtr.Zero,
            newLeft,
            newTop,
            newWidth,
            newHeight,
            SWP_NOZORDER | SWP_NOACTIVATE);

        if (updateFont)
        {
            ApplyFontScale(control, snapshot.RootClientWidth, snapshot.RootClientHeight, root);
        }

        InvalidateRect(control.Hwnd, IntPtr.Zero, true);
        UpdateWindow(control.Hwnd);
    }

    private static void ApplyFontScale(CapturedControl control, int baseRootWidth, int baseRootHeight, IntPtr root)
    {
        if (!control.Font.HasValue)
        {
            return;
        }

        int currentRootWidth;
        int currentRootHeight;
        GetClientSize(root, out currentRootWidth, out currentRootHeight);

        double scaleX = (double)currentRootWidth / Math.Max(1, baseRootWidth);
        double scaleY = (double)currentRootHeight / Math.Max(1, baseRootHeight);
        double scale = Math.Min(scaleX, scaleY);
        if (scale < 1.0)
        {
            scale = 1.0;
        }

        int originalHeight = control.Font.Value.lfHeight;
        if (originalHeight == 0)
        {
            return;
        }

        int scaledHeight = Math.Max(1, (int)Math.Round(Math.Abs(originalHeight) * scale));
        if (scaledHeight == control.LastFontHeight)
        {
            return;
        }

        LOGFONT font = control.Font.Value;
        font.lfHeight = originalHeight < 0 ? -scaledHeight : scaledHeight;

        IntPtr hFont = CreateFontIndirect(ref font);
        if (hFont == IntPtr.Zero)
        {
            return;
        }

        SendMessage(control.Hwnd, WM_SETFONT, hFont, new IntPtr(1));
        if (control.CurrentFontHandle != IntPtr.Zero)
        {
            DeleteObject(control.CurrentFontHandle);
        }

        control.CurrentFontHandle = hFont;
        control.LastFontHeight = scaledHeight;
    }

    private static LOGFONT? ReadFont(IntPtr hwnd)
    {
        IntPtr fontHandle = SendMessage(hwnd, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        if (fontHandle == IntPtr.Zero)
        {
            return null;
        }

        LOGFONT font = new LOGFONT();
        int result = GetObject(fontHandle, Marshal.SizeOf(typeof(LOGFONT)), ref font);
        if (result == 0)
        {
            return null;
        }

        return font;
    }

    private static int GetDepth(IntPtr hwnd, IntPtr root)
    {
        int depth = 0;
        IntPtr cursor = hwnd;
        while (cursor != IntPtr.Zero && cursor != root)
        {
            cursor = GetParent(cursor);
            depth++;
        }

        return depth;
    }

    private static bool IsMarkerMovePanel(IntPtr hwnd)
    {
        string className = ReadClassName(hwnd);
        if (!string.Equals(className, "ThunderRT6Frame", StringComparison.Ordinal))
        {
            return false;
        }

        string title = ReadWindowTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        int arrowChildCount = 0;
        EnumChildWindows(
            hwnd,
            delegate (IntPtr child, IntPtr lParam)
            {
                if (!IsWindowVisible(child))
                {
                    return true;
                }

                if (!string.Equals(ReadClassName(child), "AfxWnd40", StringComparison.Ordinal))
                {
                    return true;
                }

                RECT rect = new RECT();
                GetWindowRect(child, out rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width >= 100 && height >= 30 && string.IsNullOrWhiteSpace(ReadWindowTitle(child)))
                {
                    arrowChildCount++;
                }

                return arrowChildCount < 2;
            },
            IntPtr.Zero);

        return arrowChildCount >= 2;
    }

    private static void AssignBottomAnchorMetadata(CapturedControl control)
    {
        if (control.AnchorBesideTable)
        {
            return;
        }

        if (string.Equals(control.ClassName, "ThunderRT6PictureBoxDC", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(control.ClassName, "ThunderRT6Frame", StringComparison.Ordinal) &&
            (string.Equals(control.WindowTitle, "Marker Position", StringComparison.Ordinal) ||
             string.Equals(control.WindowTitle, "Scale && Shift", StringComparison.Ordinal) ||
             string.Equals(control.WindowTitle, "Measurement Parameter", StringComparison.Ordinal) ||
             string.Equals(control.WindowTitle, "Auto Results", StringComparison.Ordinal)))
        {
            return;
        }

        if (control.BaseWidth < 120 || control.BaseHeight < 60)
        {
            return;
        }

        RECT? graphRect = FindLargestGraphRect(control.Parent, control.Hwnd);
        if (!graphRect.HasValue)
        {
            return;
        }

        int gap = control.BaseTop - graphRect.Value.Bottom;
        if (gap < -10)
        {
            return;
        }

        control.AnchorBelowGraph = true;
        control.BaseGapBelowGraph = Math.Max(8, gap);
    }

    private static RECT? FindSiblingRect(IntPtr parent, IntPtr excludeHwnd, string className, string windowTitle)
    {
        RECT bestRect = new RECT();
        int bestArea = 0;

        EnumChildWindows(
            parent,
            delegate (IntPtr child, IntPtr lParam)
            {
                if (child == excludeHwnd || !IsWindowVisible(child))
                {
                    return true;
                }

                if (!string.Equals(ReadClassName(child), className, StringComparison.Ordinal))
                {
                    return true;
                }

                if (windowTitle != null && !string.Equals(ReadWindowTitle(child), windowTitle, StringComparison.Ordinal))
                {
                    return true;
                }

                RECT rect = new RECT();
                GetWindowRect(child, out rect);
                MapWindowPoints(IntPtr.Zero, parent, ref rect, 2);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestRect = rect;
                }

                return true;
            },
            IntPtr.Zero);

        return bestArea > 0 ? (RECT?)bestRect : null;
    }

    private static RECT? FindBottomContentRect(IntPtr parent, IntPtr excludeHwnd)
    {
        RECT? graphRect = FindLargestGraphRect(parent, excludeHwnd);
        if (!graphRect.HasValue)
        {
            return null;
        }

        RECT bestRect = new RECT();
        int bestArea = 0;
        int graphBottom = graphRect.Value.Bottom;

        EnumChildWindows(
            parent,
            delegate (IntPtr child, IntPtr lParam)
            {
                if (child == excludeHwnd || !IsWindowVisible(child))
                {
                    return true;
                }

                string className = ReadClassName(child);
                string title = ReadWindowTitle(child);

                if (string.Equals(className, "ThunderRT6PictureBoxDC", StringComparison.Ordinal))
                {
                    return true;
                }

                if (string.Equals(className, "ThunderRT6Frame", StringComparison.Ordinal) &&
                    (string.Equals(title, "Marker Position", StringComparison.Ordinal) ||
                     string.Equals(title, "Scale && Shift", StringComparison.Ordinal) ||
                     string.Equals(title, "Measurement Parameter", StringComparison.Ordinal) ||
                     string.Equals(title, "Auto Results", StringComparison.Ordinal)))
                {
                    return true;
                }

                RECT rect = new RECT();
                GetWindowRect(child, out rect);
                MapWindowPoints(IntPtr.Zero, parent, ref rect, 2);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int area = width * height;

                if (width < 120 || height < 60 || area < 20000)
                {
                    return true;
                }

                if (rect.Top < graphBottom - 10)
                {
                    return true;
                }

                if (area > bestArea)
                {
                    bestArea = area;
                    bestRect = rect;
                }

                return true;
            },
            IntPtr.Zero);

        return bestArea > 0 ? (RECT?)bestRect : null;
    }

    private static RECT? FindLargestGraphRect(IntPtr parent, IntPtr excludeHwnd)
    {
        RECT bestRect = new RECT();
        int bestArea = 0;

        EnumChildWindows(
            parent,
            delegate (IntPtr child, IntPtr lParam)
            {
                if (child == excludeHwnd || !IsWindowVisible(child))
                {
                    return true;
                }

                if (!string.Equals(ReadClassName(child), "ThunderRT6PictureBoxDC", StringComparison.Ordinal))
                {
                    return true;
                }

                RECT rect = new RECT();
                GetWindowRect(child, out rect);
                MapWindowPoints(IntPtr.Zero, parent, ref rect, 2);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestRect = rect;
                }

                return true;
            },
            IntPtr.Zero);

        return bestArea > 0 ? (RECT?)bestRect : null;
    }

    private static void FixMarkerMovePanel(IntPtr root)
    {
        if (MarkerMoveFixInProgress)
        {
            return;
        }

        IntPtr target = IntPtr.Zero;
        IntPtr bottomPanel = IntPtr.Zero;
        IntPtr markerPosition = IntPtr.Zero;

        EnumChildWindows(
            root,
            delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                string className = ReadClassName(hwnd);
                string title = ReadWindowTitle(hwnd);

                RECT rect = new RECT();
                GetWindowRect(hwnd, out rect);
                MapWindowPoints(IntPtr.Zero, root, ref rect, 2);

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (string.Equals(className, "ThunderRT6Frame", StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(title) &&
                    width > 300 &&
                    height > 100 &&
                    rect.Top < 700)
                {
                    target = hwnd;
                }
                else if (string.Equals(className, "SSTabCtlWndClass", StringComparison.Ordinal))
                {
                    bottomPanel = hwnd;
                }
                else if (string.Equals(className, "ThunderRT6Frame", StringComparison.Ordinal) &&
                         string.Equals(title, "Marker Position", StringComparison.Ordinal))
                {
                    markerPosition = hwnd;
                }

                return true;
            },
            IntPtr.Zero);

        if (target == IntPtr.Zero || bottomPanel == IntPtr.Zero)
        {
            return;
        }

        RECT targetRect = new RECT();
        GetWindowRect(target, out targetRect);
        MapWindowPoints(IntPtr.Zero, root, ref targetRect, 2);

        RECT bottomRect = new RECT();
        GetWindowRect(bottomPanel, out bottomRect);
        MapWindowPoints(IntPtr.Zero, root, ref bottomRect, 2);

        int widthCurrent = targetRect.Right - targetRect.Left;
        int heightCurrent = targetRect.Bottom - targetRect.Top;
        int newLeft = bottomRect.Right + 16;
        int newTop = bottomRect.Top;

        if (markerPosition != IntPtr.Zero)
        {
            RECT markerRect = new RECT();
            GetWindowRect(markerPosition, out markerRect);
            MapWindowPoints(IntPtr.Zero, root, ref markerRect, 2);
            int maxLeft = markerRect.Left - widthCurrent - 16;
            if (newLeft > maxLeft)
            {
                newLeft = maxLeft;
            }
        }

        if (targetRect.Left == newLeft && targetRect.Top == newTop)
        {
            return;
        }

        MarkerMoveFixInProgress = true;
        try
        {
            SetWindowPos(
                target,
                IntPtr.Zero,
                newLeft,
                newTop,
                widthCurrent,
                heightCurrent,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        finally
        {
            MarkerMoveFixInProgress = false;
        }
    }

    private static void InstallMarkerMoveHook(int processId, IntPtr root)
    {
        UninstallMarkerMoveHook();
        MarkerMoveRoot = root;
        MarkerMoveProcessId = processId;
        MarkerMoveHook = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE,
            EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            MarkerMoveWinEventCallback,
            (uint)processId,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private static void UninstallMarkerMoveHook()
    {
        if (MarkerMoveHook != IntPtr.Zero)
        {
            UnhookWinEvent(MarkerMoveHook);
            MarkerMoveHook = IntPtr.Zero;
        }

        MarkerMoveRoot = IntPtr.Zero;
        MarkerMoveProcessId = 0;
        MarkerMoveFixInProgress = false;
    }

    private static void OnMarkerMoveWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (MarkerMoveHook == IntPtr.Zero || MarkerMoveRoot == IntPtr.Zero || MarkerMoveFixInProgress)
        {
            return;
        }

        if (eventType != EVENT_OBJECT_LOCATIONCHANGE || hwnd == IntPtr.Zero)
        {
            return;
        }

        uint windowProcessId;
        GetWindowThreadProcessId(hwnd, out windowProcessId);
        if (windowProcessId != (uint)MarkerMoveProcessId)
        {
            return;
        }

        FixMarkerMovePanel(MarkerMoveRoot);
    }

    private static void PatchFrame(IntPtr hwnd)
    {
        int style = GetWindowStyle(hwnd);
        int newStyle = style | WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;

        if (newStyle != style)
        {
            SetWindowStyle(hwnd, newStyle);
        }

        IntPtr systemMenu = GetSystemMenu(hwnd, false);
        if (systemMenu != IntPtr.Zero)
        {
            EnableMenuItem(systemMenu, SC_MAXIMIZE, MF_BYCOMMAND | MF_ENABLED);
            EnableMenuItem(systemMenu, SC_SIZE, MF_BYCOMMAND | MF_ENABLED);
            DrawMenuBar(hwnd);
        }

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    private static IntPtr WaitForTargetWindow(int processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int stableCount = 0;
        IntPtr lastWindow = IntPtr.Zero;
        int lastChildren = -1;

        while (DateTime.UtcNow < deadline)
        {
            IntPtr hwnd = FindTargetWindow(processId);
            if (hwnd != IntPtr.Zero)
            {
                int childCount = CountVisibleChildren(hwnd);
                if (hwnd == lastWindow && childCount == lastChildren && childCount > 20)
                {
                    stableCount++;
                    if (stableCount >= 4)
                    {
                        return hwnd;
                    }
                }
                else
                {
                    stableCount = 0;
                    lastWindow = hwnd;
                    lastChildren = childCount;
                }
            }

            Thread.Sleep(250);
        }

        return lastWindow;
    }

    private static int CountVisibleChildren(IntPtr root)
    {
        int count = 0;
        EnumChildWindows(
            root,
            delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (IsWindowVisible(hwnd))
                {
                    count++;
                }

                return true;
            },
            IntPtr.Zero);

        return count;
    }

    private static IntPtr FindTargetWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows(
            delegate (IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                uint windowProcessId;
                GetWindowThreadProcessId(hwnd, out windowProcessId);
                if (windowProcessId == 0 || windowProcessId != (uint)processId)
                {
                    return true;
                }

                if (!string.Equals(ReadClassName(hwnd), TargetWindowClass, StringComparison.Ordinal))
                {
                    return true;
                }

                string title = ReadWindowTitle(hwnd);
                if (!title.StartsWith(TargetWindowTitle, StringComparison.Ordinal))
                {
                    return true;
                }

                found = hwnd;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForProcessExit(int processId)
    {
        while (IsProcessAlive(processId))
        {
            Thread.Sleep(300);
        }
    }

    private static void GetClientSize(IntPtr hwnd, out int width, out int height)
    {
        RECT rect = new RECT();
        GetClientRect(hwnd, out rect);
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
    }

    private static string BuildArguments(string[] args)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(args[i]));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        bool needsQuotes = arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needsQuotes)
        {
            return arg;
        }

        var builder = new StringBuilder();
        builder.Append('"');

        int backslashes = 0;
        foreach (char ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(ch);
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        int length = GetClassName(hwnd, builder, builder.Capacity);
        return length <= 0 ? string.Empty : builder.ToString();
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        int length = GetWindowText(hwnd, builder, builder.Capacity);
        return length <= 0 ? string.Empty : builder.ToString();
    }

    private static int GetWindowStyle(IntPtr hwnd)
    {
        if (IntPtr.Size == 8)
        {
            return unchecked((int)GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64());
        }

        return GetWindowLong32(hwnd, GWL_STYLE);
    }

    private static void SetWindowStyle(IntPtr hwnd, int style)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(style));
            return;
        }

        SetWindowLong32(hwnd, GWL_STYLE, style);
    }

    private sealed class LayoutSnapshot
    {
        public IntPtr Root;
        public int RootClientWidth;
        public int RootClientHeight;
        public readonly List<CapturedControl> Controls = new List<CapturedControl>();
        public readonly Dictionary<IntPtr, SizePair> BaseClientSizes = new Dictionary<IntPtr, SizePair>();
        public readonly HashSet<long> KnownControlHandles = new HashSet<long>();

        public void DisposeFonts()
        {
            for (int i = 0; i < Controls.Count; i++)
            {
                if (Controls[i].CurrentFontHandle != IntPtr.Zero)
                {
                    DeleteObject(Controls[i].CurrentFontHandle);
                    Controls[i].CurrentFontHandle = IntPtr.Zero;
                }
            }
        }
    }

    private sealed class CapturedControl
    {
        public IntPtr Hwnd;
        public IntPtr Parent;
        public int Depth;
        public int BaseLeft;
        public int BaseTop;
        public int BaseWidth;
        public int BaseHeight;
        public LOGFONT? Font;
        public int LastFontHeight;
        public IntPtr CurrentFontHandle;
        public string ClassName;
        public string WindowTitle;
        public bool AnchorBesideTable;
        public bool AnchorBelowGraph;
        public int BaseGapBelowGraph;
    }

    private struct SizePair
    {
        public readonly int Width;
        public readonly int Height;

        public SizePair(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref RECT lpPoints, int cPoints);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern int EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFontIndirect(ref LOGFONT lplf);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref LOGFONT lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
