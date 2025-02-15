﻿/*
 *  Copyright (C) 2018 - 2024 iSnackyCracky, NETertainer
 *
 *  This file is part of KeePassRDP.
 *
 *  KeePassRDP is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  KeePassRDP is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with KeePassRDP.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

using KeePass.Plugins;
using KeePass.Resources;
using KeePass.UI;
using KeePass.Util.Spr;
using KeePassLib;
using KeePassRDP.Commands;
using KeePassRDP.Extensions;
using KeePassRDP.Generator;
using KeePassRDP.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace KeePassRDP
{
    internal class KprConnectionManager : IDisposable
    {
        private class TaskWithCancellationToken : Task
        {
            public bool IsCancellationRequested { get { return _cancellationTokenSource.IsCancellationRequested; } }

            private readonly CancellationTokenSource _cancellationTokenSource;

            public TaskWithCancellationToken(Action<object> action, object state, CancellationTokenSource cancellationTokenSource) : base(
                action,
                state,
                cancellationTokenSource.Token,
                TaskCreationOptions.AttachedToParent |
                TaskCreationOptions.PreferFairness |
                TaskCreationOptions.LongRunning)
            {
                _cancellationTokenSource = cancellationTokenSource;
                Start();
            }

            public void Cancel()
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                    _cancellationTokenSource.Cancel();
            }

            public new void Start()
            {
                Start(TaskScheduler.Default);
            }

            public new Task ContinueWith(Action<Task> action)
            {
                return ContinueWith(action, CancellationToken.None, TaskContinuationOptions.PreferFairness, TaskScheduler.Default);
            }

            public new void Dispose()
            {
                if (!IsCompleted)
                {
                    if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                        _cancellationTokenSource.Cancel();
                    try
                    {
                        Wait(TimeSpan.FromSeconds(10));
                    }
                    catch
                    {
                    }
                }

                if (_cancellationTokenSource != null)
                    _cancellationTokenSource.Dispose();

                if (IsCompleted)
                    base.Dispose();
            }
        }

        public int Count { get { return _tasks.Count; } }

        public bool IsCompleted
        {
            get
            {
                return !_tasks.Values.Any(x =>
                {
                    try
                    {
                        return !x.IsCompleted;
                    }
                    catch
                    {
                        return true;
                    }
                });
            }
        }

        internal Lazy<KprCredentialPicker> CredentialPicker { get { return _credentialPicker; } }

        private readonly IPluginHost _host;
        private readonly KprConfig _config;
        private readonly Lazy<KprCredentialManager<KprCredential>> _credManager;
        private readonly Lazy<KprCredentialPicker> _credentialPicker;
        private readonly ConcurrentDictionary<string, TaskWithCancellationToken> _tasks;

        public KprConnectionManager(IPluginHost host, KprConfig config, Lazy<KprCredentialManager<KprCredential>> credManager)
        {
            _host = host;
            _config = config;
            _credManager = credManager;
            _credentialPicker = new Lazy<KprCredentialPicker>(() => new KprCredentialPicker(_host, _config), LazyThreadSafetyMode.ExecutionAndPublication);
            _tasks = new ConcurrentDictionary<string, TaskWithCancellationToken>(4, 0);
        }

        public bool Wait(int seconds = Timeout.Infinite)
        {
            try
            {
                return Task.WaitAll(_tasks.Values.Where(x =>
                {
                    try
                    {
                        return !x.IsCompleted;
                    }
                    catch
                    {
                        return false;
                    }
                }).ToArray(), TimeSpan.FromSeconds(seconds));
            }
            catch
            {
                return IsCompleted;
            }
        }

        public void Cancel()
        {
            foreach (var task in _tasks.Values.Where(x =>
            {
                try
                {
                    return !x.IsCompleted && !x.IsCancellationRequested;
                }
                catch
                {
                    return false;
                }
            }))
                try
                {
                    task.Cancel();
                }
                catch
                {
                }
        }

        public void Dispose()
        {
            _tasks.Clear();

            if (_credentialPicker.IsValueCreated)
                _credentialPicker.Value.Dispose();
        }

        public void ConnectRDPtoKeePassEntry(bool tmpMstscUseAdmin = false, bool tmpUseCreds = false)
        {
            if (Util.IsValid(_host))
            {
                var mainForm = _host.MainWindow;
                var selectedEntries = mainForm.GetSelectedEntries();

                var parentGroups = selectedEntries
                    .Where(entry => !entry.Strings.GetSafe(PwDefs.UrlField).IsEmpty)
                    .GroupBy(entry => entry.ParentGroup.Uuid, EqualityComparer<PwUuid>.Default);

                if (!_config.KeePassConnectToAll)
                    parentGroups = parentGroups.Skip(parentGroups.Count() - 1);

                var totalCount = parentGroups.Aggregate(0, (a, b) => a + b.Count());

                var postfix = string.Format(KprResourceManager.Instance["{0} entr" + (totalCount == 1 ? "y" : "ies") + " of {1} selected."], totalCount, selectedEntries.Length);
                var connectingTo = string.Format(" {0} ", KprResourceManager.Instance["connecting to"]);
                var skipped = string.Format(" {0}", KprResourceManager.Instance["skipped"]);

                mainForm.SetStatusEx(string.Format("{0}{1}{2}", Util.KeePassRDP, connectingTo, postfix));

                foreach (var parentGroup in parentGroups)
                {
                    var entries = parentGroup.AsEnumerable();
                    var count = entries.Count();

                    if (count == 0)
                        continue;

                    if (!_config.KeePassConnectToAll && count > 1)
                    {
                        entries = entries.Skip(count - 1);
                        count = 1;
                    }

                    if (totalCount > 1)
                        mainForm.SetStatusEx(string.Format("{0}{1}{2} / {3}", Util.KeePassRDP, connectingTo, count, postfix));

                    PwEntry credEntry = null;

                    var skippedEntries = new List<PwEntry>();

                    // Connect to RDP using credentials from KeePass, skipping entries with no credentials.
                    if (tmpUseCreds)
                    {
                        var credPick = _credentialPicker.Value;
                        var usedCount = 0;

                        // Fill KprCredentialPicker with selected entries.
                        foreach (var entry in entries)
                            using (var entrySettings = entry.GetKprSettings(true) ?? KprEntrySettings.Empty)
                                if (entrySettings.UseCredpicker && (Util.InRdpSubgroup(entry, _config.CredPickerCustomGroup) || entrySettings.CpGroupUUIDs.Any()))
                                {
                                    credPick.AddEntry(entry, entrySettings, _config.CredPickerCustomGroup);
                                    usedCount++;
                                }
                                else
                                    skippedEntries.Add(entry);

                        if (usedCount > 0)
                        {
                            if (totalCount > 1 && usedCount != count)
                                mainForm.SetStatusEx(string.Format("{0}{1}{2} ({3}{4}) / {5}", Util.KeePassRDP, connectingTo, usedCount, count - usedCount, skipped, postfix));

                            // Get result from KprCredentialPickerForm.
                            try
                            {
                                credEntry = credPick.GetCredentialEntry(_config.CredPickerIncludeSelected, _config.KeePassShowResolvedReferences);
                            }
                            catch (OperationCanceledException)
                            {
                                continue;
                            }
                            finally
                            {
                                credPick.Reset();
                            }

                            if (credEntry == null)
                            {
                                // Skip group if no credential entries where found and no selected entry has at least a username.
                                if (!entries.Any(entry => !entry.Strings.GetSafe(PwDefs.UserNameField).IsEmpty))
                                    continue;
                            }
                            else
                            {
                                credEntry = Util.IsEntryIgnored(credEntry) ?
                                    null :
                                    credEntry.GetResolvedReferencesEntry(new SprContext(credEntry, mainForm.ActiveDatabase, _config.KeePassSprCompileFlags)
                                    {
                                        ForcePlainTextPasswords = true // true is default, PwDefs.PasswordField is replaced with PwDefs.HiddenPassword during SprEngine.Compile otherwise.
                                    });
                            }
                        }
                    }

                    foreach (var connPwEntry in entries)
                    {
                        var ctx = new SprContext(connPwEntry, mainForm.ActiveDatabase, _config.KeePassSprCompileFlags)
                        {
                            ForcePlainTextPasswords = true
                        };

                        var host = SprEngine.Compile(connPwEntry.Strings.ReadSafe(PwDefs.UrlField), ctx);

                        if (string.IsNullOrEmpty(host))
                            continue;

                        var port = string.Empty;
                        Uri uri = null;

                        // Try to parse entry URL as URI.
                        if (!Uri.TryCreate(host, UriKind.Absolute, out uri) ||
                            (uri.HostNameType == UriHostNameType.Unknown && !UriParser.IsKnownScheme(uri.Scheme)))
                        {
                            try
                            {
                                // Second try to parse entry URL as URI with UriBuilder.
                                uri = new UriBuilder(host).Uri;
                                if (uri.HostNameType == UriHostNameType.Unknown && !UriParser.IsKnownScheme(uri.Scheme))
                                    // Third try to parse entry URL as URI with fallback scheme.
                                    if (!Uri.TryCreate(string.Format("rdp://{0}", host), UriKind.Absolute, out uri) || uri.HostNameType == UriHostNameType.Unknown)
                                        uri = null;
                            }
                            catch (UriFormatException)
                            {
                                uri = null;
                            }
                        }

                        if (uri != null && uri.HostNameType != UriHostNameType.Unknown)
                        {
                            host = uri.Host;
                            if (!uri.IsDefaultPort && uri.Port != Util.DefaultRdpPort)
                                port = string.Format(":{0}", uri.Port);
                        }
                        else
                        {
                            VistaTaskDialog.ShowMessageBoxEx(
                                string.Format(KprResourceManager.Instance["The URL/target '{0}' of the selected entry could not be parsed."], host),
                                null,
                                string.Format("{0} - {1}", Util.KeePassRDP, KPRes.Error),
                                VtdIcon.Error,
                                _host.MainWindow,
                                null, 0, null, 0);
                            continue;
                        }

                        var taskUuid = connPwEntry.Uuid.ToHexString();

                        using (var entrySettings = connPwEntry.GetKprSettings(true) ?? KprEntrySettings.Empty)
                        {
                            KprCredential cred = null;

                            // Connect to RDP using credentials from KeePass, skipping entries with no credentials.
                            if (tmpUseCreds)
                            {
                                // Use result from KprCredentialPicker or fallback to credentials from selected entry.
                                var shownInPicker = entrySettings.UseCredpicker &&
                                    (Util.InRdpSubgroup(connPwEntry, _config.CredPickerCustomGroup) || entrySettings.CpGroupUUIDs.Any());
                                var tmpEntry = credEntry != null && shownInPicker ? credEntry :
                                    entrySettings.Ignore || (credEntry != null && !shownInPicker) ? null :
                                        skippedEntries.Contains(connPwEntry) ?
                                        connPwEntry.GetResolvedReferencesEntry(ctx) :
                                        null;

                                if (tmpEntry == null)
                                    continue;

                                taskUuid = string.Format("{0}-{1}", taskUuid, tmpEntry.Uuid.ToHexString());

                                var username = tmpEntry.Strings.GetSafe(PwDefs.UserNameField);
                                // Do not connect to entry if username is empty.
                                if (username.IsEmpty)
                                {
                                    if (totalCount == 1)
                                        VistaTaskDialog.ShowMessageBoxEx(
                                            KprResourceManager.Instance["Username is required when connecting with credentials."],
                                            null,
                                            Util.KeePassRDP,
                                            VtdIcon.Information,
                                            _host.MainWindow,
                                            null, 0, null, 0);
                                    continue;
                                }

                                var password = tmpEntry.Strings.GetSafe(PwDefs.PasswordField);
                                // Do not connect to entry if password is empty.
                                /*if (password.IsEmpty)
                                    continue;*/

                                if (entrySettings.ForceLocalUser)
                                    username = username.ForceLocalUser(host);

                                // Create new KprCredential.
                                cred = new KprCredential(
                                    username,
                                    password,
                                    host,
                                    _config.CredVaultUseWindows ?
                                        NativeCredentials.CRED_TYPE.DOMAIN_PASSWORD :
                                        NativeCredentials.CRED_TYPE.GENERIC,
                                    _config.CredVaultTtl);

                                //username = username.Remove(0, username.Length);
                                //password = password.Remove(0, password.Length);

                                // Add KprCredential to KprCredentialManager.
                                //_credManager.Value.Add(cred);
                            }

                            if ((tmpUseCreds || _config.KeePassAlwaysConfirm) && _tasks.ContainsKey(taskUuid) && !_tasks[taskUuid].IsCompleted)
                                if (VistaTaskDialog.ShowMessageBoxEx(
                                    tmpUseCreds ?
                                        string.Format(KprResourceManager.Instance["Already connected with the same credentials to URL/target '{0}'."], host) :
                                        string.Format(KprResourceManager.Instance["Already connected to URL/target '{0}'."], host),
                                    KprResourceManager.Instance["Continue?"],
                                    Util.KeePassRDP,
                                    VtdIcon.Information,
                                    _host.MainWindow,
                                    KprResourceManager.Instance["&Yes"], 0,
                                    KprResourceManager.Instance["&No"], 1) == 1)
                                {
                                    if (cred != null)
                                    {
                                        cred.Dispose();
                                        cred = null;
                                    }
                                    continue;
                                }

                            var command = new MstscCommand
                            {
                                HostPort = host + port
                            };

                            /*var argumentsBuilder = new StringBuilder();
                            argumentsBuilder.Append("/v:");
                            argumentsBuilder.Append(host);
                            argumentsBuilder.Append(port);*/

                            if (entrySettings.IncludeDefaultParameters)
                            {
                                if (tmpMstscUseAdmin || _config.MstscUseAdmin)
                                {
                                    command.Admin = true; // argumentsBuilder.Append(" /admin");
                                    if (_config.MstscUseRestrictedAdmin)
                                        command.RestrictedAdmin = true; // argumentsBuilder.Append(" /restrictedAdmin");
                                }
                                if (_config.MstscUsePublic)
                                    command.Public = true; // argumentsBuilder.Append(" /public");
                                if (_config.MstscUseRemoteGuard)
                                    command.RemoteGuard = true; // argumentsBuilder.Append(" /remoteGuard");
                                if (_config.MstscUseFullscreen)
                                    command.Fullscreen = true; // argumentsBuilder.Append(" /f");
                                if (_config.MstscUseSpan)
                                    command.Span = true; // argumentsBuilder.Append(" /span");
                                if (_config.MstscUseMultimon)
                                    command.Multimon = true; // argumentsBuilder.Append(" /multimon");
                                if (_config.MstscWidth > 0)
                                {
                                    /*argumentsBuilder.Append(" /w:");
                                    argumentsBuilder.Append(_config.MstscWidth);*/
                                    command.Width = _config.MstscWidth;
                                }
                                if (_config.MstscHeight > 0)
                                {
                                    /*argumentsBuilder.Append(" /h:");
                                    argumentsBuilder.Append(_config.MstscHeight);*/
                                    command.Height = _config.MstscHeight;
                                }
                            }
                            else if (tmpMstscUseAdmin)
                            {
                                command.Admin = true; // argumentsBuilder.Append(" /admin");
                            }

                            RdpFile rdpFile = null;
                            if (entrySettings.RdpFile != null)
                            {
                                rdpFile = new RdpFile(entrySettings.RdpFile);
                                command.Filename = rdpFile.ToString();
                            }

                            var argumentsBuilder = new StringBuilder(command.ToString());

                            foreach (var argument in entrySettings.MstscParameters)
                            {
                                argumentsBuilder.Append(argument);
                                argumentsBuilder.Append(' ');
                            }

                            if (argumentsBuilder.Length == 0)
                            {
                                if (cred != null)
                                {
                                    cred.Dispose();
                                    cred = null;
                                }

                                continue;
                            }

                            _tasks[taskUuid] = new TaskWithCancellationToken(thisTaskUuid =>
                            {
                                var waitForRdpFile = rdpFile != null;

                                try
                                {
                                    // Start RDP / mstsc.exe.
                                    var rdpProcess = new ProcessStartInfo
                                    {
                                        WindowStyle = ProcessWindowStyle.Normal,
                                        FileName = command.ExecutablePath, // KeePassRDPExt.MstscPath,
                                        Arguments = argumentsBuilder.ToString().TrimEnd(),
                                        ErrorDialog = true,
                                        ErrorDialogParentHandle = Form.ActiveForm.Handle,
                                        LoadUserProfile = false,
                                        WorkingDirectory = Path.GetTempPath() // Environment.ExpandEnvironmentVariables("%TEMP%")
                                    };

                                    var title = string.Empty;
                                    if (_config.MstscReplaceTitle)
                                    {
                                        title = SprEngine.Compile(connPwEntry.Strings.ReadSafe(PwDefs.TitleField), ctx);
                                        title = string.Format("{0} - {1}", string.IsNullOrEmpty(title) ? host : title, Util.KeePassRDP);
                                    }

                                    // Add KprCredential to KprCredentialManager.
                                    if (cred != null)
                                        _credManager.Value.Add(cred);

                                    using (var process = Process.Start(rdpProcess))
                                    {
                                        var inc = _config.CredVaultTtl > 0 && _config.CredVaultAdaptiveTtl && cred != null ?
                                            TimeSpan.FromSeconds(Math.Ceiling(Math.Max(1, _config.CredVaultTtl / 2f))) : TimeSpan.Zero;

                                        var ttl = (int)Math.Max(1000, inc.TotalMilliseconds);

                                        if (process.WaitForInputIdle(ttl))
                                            // Wait a limited time for mstsc.exe window, otherwise assume something went wrong.
                                            for (var spins = ttl / 250; spins > 0; spins--)
                                                if (process.MainWindowHandle != IntPtr.Zero || !process.WaitForExit(200))
                                                {
                                                    process.Refresh();

                                                    if (process.MainWindowHandle == IntPtr.Zero &&
                                                        !SpinWait.SpinUntil(() =>
                                                        {
                                                            process.Refresh();
                                                            return process.MainWindowHandle != IntPtr.Zero;
                                                        }, 50))
                                                    {
                                                        // Keep incrementing TTL as necessary.
                                                        if (cred != null)
                                                            cred.IncreaseTTL(inc);
                                                        continue;
                                                    }

                                                    var oldTitle = process.MainWindowTitle;

                                                    if (!string.IsNullOrEmpty(title))
                                                        NativeMethods.SetWindowText(process.MainWindowHandle, title);

                                                    // Find progress bar.
                                                    var pbHandle = NativeMethods.FindWindowEx(process.MainWindowHandle, IntPtr.Zero, "msctls_progress32", null);

                                                    if (waitForRdpFile)
                                                    {
                                                        // Check for (un-)signed .rdp file dialog.
                                                        var ms = (int)TimeSpan.FromMilliseconds(750).TotalMilliseconds;
                                                        if (!process.HasExited && !process.WaitForExit(ms))
                                                        {
                                                            process.Refresh();

                                                            if (process.MainWindowHandle != IntPtr.Zero)
                                                            {
                                                                var lastPopup = NativeMethods.GetLastActivePopup(process.MainWindowHandle);

                                                                // Continue when popup is open.
                                                                if (lastPopup != process.MainWindowHandle)
                                                                {
                                                                    var element = AutomationElement.FromHandle(lastPopup);
                                                                    var button = element.FindFirst(
                                                                        TreeScope.Children,
                                                                        new PropertyCondition(AutomationElement.AutomationIdProperty, "13498"));

                                                                    if (button != null && button.Current.ControlType == ControlType.Image)
                                                                    {
                                                                        if (_config.MstscConfirmCertificate)
                                                                        {
                                                                            button = element.FindFirst(
                                                                                TreeScope.Children,
                                                                                new PropertyCondition(AutomationElement.AutomationIdProperty, "1"));

                                                                            if (button != null && button.Current.ControlType == ControlType.Button)
                                                                            {
                                                                                var buttonHandle = new IntPtr(button.Current.NativeWindowHandle);

                                                                                // Try LegacyIAccessible.DoDefaultAction() first, fallback to emulating click on button.
                                                                                try
                                                                                {
                                                                                    if (KprDoDefaultAction(buttonHandle) != 0)
                                                                                        throw new Exception();
                                                                                }
                                                                                catch
                                                                                {
                                                                                    NativeMethods.SendMessage(buttonHandle, NativeMethods.WM_LBUTTONDOWN, 0, 0);
                                                                                    NativeMethods.SendMessage(buttonHandle, NativeMethods.WM_LBUTTONUP, 0, 0);
                                                                                    NativeMethods.SendMessage(buttonHandle, NativeMethods.BM_CLICK, 0, 0);
                                                                                }

                                                                                waitForRdpFile = false;
                                                                                continue;
                                                                            }
                                                                        }

                                                                        continue;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }

                                                    if (pbHandle != IntPtr.Zero)
                                                    {
                                                        var ms = (int)TimeSpan.FromMilliseconds(750).TotalMilliseconds;
                                                        do
                                                        {
                                                            if (process.HasExited || process.WaitForExit(ms))
                                                                break;

                                                            process.Refresh();

                                                            if (process.MainWindowHandle == IntPtr.Zero)
                                                                break;

                                                            var lastPopup = NativeMethods.GetLastActivePopup(process.MainWindowHandle);

                                                            // Continue when popup is open.
                                                            if (lastPopup != process.MainWindowHandle)
                                                            {
                                                                var element = AutomationElement.FromHandle(lastPopup);

                                                                // Connection failed error box.
                                                                var button = element.FindFirst(
                                                                    TreeScope.Children,
                                                                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CommandButton_1"));

                                                                if (button != null && button.Current.ControlType == ControlType.Button)
                                                                    break;

                                                                if (_config.MstscConfirmCertificate)
                                                                {
                                                                    // Confirm certificate error box.
                                                                    button = element.FindFirst(
                                                                        TreeScope.Children,
                                                                        new PropertyCondition(AutomationElement.AutomationIdProperty, "14004"));

                                                                    if (button != null && button.Current.ControlType == ControlType.Button)
                                                                    {
                                                                        var buttonHandle = new IntPtr(button.Current.NativeWindowHandle);

                                                                        // Try LegacyIAccessible.DoDefaultAction() first, fallback to emulating click on button.
                                                                        try
                                                                        {
                                                                            if (KprDoDefaultAction(buttonHandle) != 0)
                                                                                throw new Exception();
                                                                        }
                                                                        catch
                                                                        {
                                                                            NativeMethods.SendMessage(buttonHandle, NativeMethods.WM_LBUTTONDOWN, 0, 0);
                                                                            NativeMethods.SendMessage(buttonHandle, NativeMethods.WM_LBUTTONUP, 0, 0);
                                                                            NativeMethods.SendMessage(buttonHandle, NativeMethods.BM_CLICK, 0, 0);
                                                                        }

                                                                        continue;
                                                                    }
                                                                }
                                                            }
                                                            else
                                                            {
                                                                // Break when progress bar is gone.
                                                                //if (NativeMethods.FindWindowEx(process.MainWindowHandle, IntPtr.Zero, "BBarWindowClass", "BBar") != IntPtr.Zero)
                                                                if (NativeMethods.FindWindowEx(process.MainWindowHandle, IntPtr.Zero, "msctls_progress32", null) == IntPtr.Zero)
                                                                    break;
                                                            }

                                                            // Keep incrementing TTL as necessary.
                                                            if (cred != null)
                                                                cred.IncreaseTTL(inc);
                                                        } while (!process.HasExited);
                                                    }
                                                    else if (spins == 1 && cred != null && (_config.CredVaultRemoveOnExit || (_config.CredVaultTtl > 0 && _config.CredVaultAdaptiveTtl)))
                                                    {
                                                        cred.Dispose();
                                                        cred = null;
                                                    }
                                                }
                                                else
                                                    break;

                                        if (_config.CredVaultTtl > 0 && _config.CredVaultAdaptiveTtl && cred != null)
                                        {
                                            cred.Dispose();
                                            cred = null;
                                        }

                                        if (!process.HasExited)
                                        {
                                            process.Refresh();
                                            if (!string.IsNullOrEmpty(title) && process.MainWindowHandle != IntPtr.Zero)
                                                NativeMethods.SetWindowText(process.MainWindowHandle, title);

                                            if (!process.HasExited && !process.WaitForExit(200))
                                            {
                                                process.Refresh();

                                                // Set title twice to try to make sure to catch the right window handle.
                                                if (!string.IsNullOrEmpty(title) && process.MainWindowHandle != IntPtr.Zero)
                                                    NativeMethods.SetWindowText(process.MainWindowHandle, title);

                                                var timeout = 5000;
                                                // Check if a window is still alive from time to time.
                                                while (!process.WaitForExit(timeout))
                                                {
                                                    process.Refresh();

                                                    // Assume something went wrong when there is no window anymore.
                                                    if (process.MainWindowHandle == IntPtr.Zero)
                                                    {
                                                        if (!process.HasExited)
                                                            process.Kill();
                                                        break;
                                                    }

                                                    // Assume something went wrong when threads get stuck.
                                                    /*var allThreads = process.Threads.Cast<ProcessThread>();
                                                    if (!allThreads.Any(thread => thread.ThreadState != System.Diagnostics.ThreadState.Wait) &&
                                                         allThreads.Any(thread => thread.WaitReason != ThreadWaitReason.Suspended))
                                                    {
                                                        if (!process.HasExited)
                                                            process.Kill();
                                                        break;
                                                    }*/

                                                    if (timeout < 60000)
                                                        timeout += 5000;
                                                }
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    if (cred != null)
                                    {
                                        if (_config.CredVaultTtl > 0 && _config.CredVaultAdaptiveTtl)
                                            cred.ResetTTL();
                                        if (_config.CredVaultRemoveOnExit)
                                            cred.Dispose();
                                    }

                                    if (rdpFile != null)
                                        rdpFile.Dispose();
                                }

                                TaskWithCancellationToken oldtask;
                                if (_tasks.TryRemove((string)thisTaskUuid, out oldtask))
                                    oldtask.ContinueWith(t =>
                                    {
                                        try
                                        {
                                            if (!t.IsCompleted)
                                                t.Wait();
                                        }
                                        finally
                                        {
                                            oldtask.Dispose();
                                        }
                                    });
                            }, taskUuid, new CancellationTokenSource());
                        }
                    }
                }

                mainForm.SetStatusEx(null);
            }
        }

        [DllImport("KeePassRDP.unmanaged.dll", EntryPoint = "KprDoDefaultAction", SetLastError = false)]
        private static extern int KprDoDefaultAction([In] IntPtr parent);

        /*[DllImport("KeePassRDP.unmanaged.dll", EntryPoint = "KprDoDefaultAction", SetLastError = false)]
        private static extern int KprDoDefaultAction([In] IntPtr parent, [In] string automationId);*/
    }
}