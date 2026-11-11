using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Launcher : ObservableObject
    {
        // All open launcher windows. The first one is the primary window.
        public static IReadOnlyList<Launcher> All => _all;

        // The launcher whose window was most recently activated.
        public static Launcher Current
        {
            get => _current ?? Primary;
            set => _current = value;
        }

        public static Launcher Primary => _all.Count > 0 ? _all[0] : null;

        // True while the app is quitting or switching workspace: closing windows must not drop their repos from the workspace.
        public static bool KeepWorkspaceOnClose { get; set; }

        public bool IsPrimary => _all.Count > 0 && _all[0] == this;

        public event Action CloseWindowRequested;
        public event Action BringToTopRequested;

        public AvaloniaList<LauncherPage> Pages
        {
            get;
            private set;
        }

        public Workspace ActiveWorkspace
        {
            get => _activeWorkspace;
            private set => SetProperty(ref _activeWorkspace, value);
        }

        public LauncherPage ActivePage
        {
            get => _activePage;
            set
            {
                if (SetProperty(ref _activePage, value))
                    PostActivePageChanged();
            }
        }

        public ICommandPalette CommandPalette
        {
            get => _commandPalette;
            set => SetProperty(ref _commandPalette, value);
        }

        public Models.Version NewVersion
        {
            get => _newVersion;
            set => SetProperty(ref _newVersion, value);
        }

        public Launcher(string startupRepo)
        {
            _all.Add(this);
            Models.Notification.Raised += DispatchNotification;
            _ignoreIndexChange = true;

            ActiveWorkspace = Preferences.Instance.GetActiveWorkspace();
            Pages = new AvaloniaList<LauncherPage>();
            AddNewTab();

            var repos = ActiveWorkspace.Repositories.ToArray();
            foreach (var repo in repos)
                OpenRepositoryInTab(repo, null);

            _ignoreIndexChange = false;

            if (!TryOpenRepositoryFromPath(startupRepo))
            {
                var activeIdx = ActiveWorkspace.ActiveIdx;
                if (activeIdx > 0 && activeIdx < Pages.Count)
                    ActivePage = Pages[activeIdx];
                else
                    ActivePage = Pages[0];
            }

            PostActivePageChanged();
        }

        // Creates a secondary launcher (window) that takes over `page`.
        private Launcher(LauncherPage page)
        {
            _all.Add(this);
            ActiveWorkspace = Primary.ActiveWorkspace;
            Pages = new AvaloniaList<LauncherPage> { page };
            _activePage = page;
        }

        public void MakeCurrent()
        {
            _current = this;
        }

        public void RequestBringToTop()
        {
            BringToTopRequested?.Invoke();
        }

        public bool CanDetach(LauncherPage page)
        {
            return page is { Data: Repository };
        }

        // Moves `page` out of this launcher into a brand new one.
        public Launcher DetachToNewLauncher(LauncherPage page)
        {
            if (!DetachTab(page))
                return null;

            var launcher = new Launcher(page);
            SyncWorkspace();
            return launcher;
        }

        // Moves `page` from `from` into `to`, placed before `before` (or at the end).
        public static void MoveTabAcross(Launcher from, LauncherPage page, Launcher to, LauncherPage before)
        {
            if (from == to || !from.DetachTab(page))
                return;

            var idx = before == null ? -1 : to.Pages.IndexOf(before);
            if (idx < 0)
                to.Pages.Add(page);
            else
                to.Pages.Insert(idx, page);

            to.ActivePage = page;
            SyncWorkspace();
        }

        public static LauncherPage FindPage(string id, out Launcher owner)
        {
            foreach (var l in _all)
            {
                foreach (var p in l.Pages)
                {
                    if (p.Node.Id.Equals(id, StringComparison.Ordinal))
                    {
                        owner = l;
                        return p;
                    }
                }
            }

            owner = null;
            return null;
        }

        private bool DetachTab(LauncherPage page)
        {
            var idx = Pages.IndexOf(page);
            if (idx < 0)
                return false;

            _ignoreIndexChange = true;

            if (_activePage == page && Pages.Count > 1)
                ActivePage = Pages[idx > 0 ? idx - 1 : idx + 1];

            Pages.RemoveAt(idx);

            if (Pages.Count == 0)
            {
                if (IsPrimary)
                {
                    AddNewTab();
                }
                else
                {
                    _all.Remove(this);
                    if (_current == this)
                        _current = null;
                    CloseWindowRequested?.Invoke();
                }
            }

            _ignoreIndexChange = false;
            PostActivePageChanged();
            return true;
        }

        // Rebuild the workspace's repository list from the tabs of all windows.
        private static void SyncWorkspace()
        {
            var ws = Primary?.ActiveWorkspace;
            if (ws == null)
                return;

            ws.Repositories.Clear();
            foreach (var l in _all)
            {
                foreach (var p in l.Pages)
                {
                    if (p.Data is Repository r)
                        ws.Repositories.Add(r.FullPath);
                }
            }
        }

        public bool TryOpenRepositoryFromPath(string repo)
        {
            if (!string.IsNullOrEmpty(repo) && Directory.Exists(repo))
            {
                var isBare = new Commands.IsBareRepository(repo).GetResult();
                if (isBare)
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(repo, null, false);
                    Welcome.Instance.Refresh();
                    OpenRepositoryInTab(node, null);
                    return true;
                }

                var test = new Commands.QueryRepositoryRootPath(repo).GetResult();
                if (test.IsSuccess && !string.IsNullOrEmpty(test.StdOut))
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(test.StdOut.Trim(), null, false);
                    Welcome.Instance.Refresh();
                    OpenRepositoryInTab(node, null);
                    return true;
                }
                else
                {
                    if (ActivePage is not { Data: Welcome { }, Popup: null })
                        AddNewTab();

                    if (Models.SafeDirectories.IsUntrustedRepository(test.StdErr) &&
                        Models.SafeDirectories.TryGetSafeDirectoryValue(repo, test.StdErr, out var safeDirectory))
                        ActivePage.Popup = new TrustRepository(ActivePage.Node.Id, repo, test.StdErr, safeDirectory, null, false, true, 0);
                    else
                        ActivePage.Popup = new Init(ActivePage.Node.Id, repo, null, 0, test.StdErr ?? "Unknown error occurred while opening the repository.");

                    return true;
                }
            }

            return false;
        }

        public void CloseAll()
        {
            _ignoreIndexChange = true;

            var remove = !IsPrimary && !KeepWorkspaceOnClose;
            foreach (var one in Pages)
                CloseRepositoryInTab(one, remove);

            _ignoreIndexChange = false;

            if (!IsPrimary)
            {
                _all.Remove(this);
                if (_current == this)
                    _current = null;
            }
        }

        public void SwitchWorkspace(Workspace to)
        {
            if (!IsPrimary)
            {
                Primary.SwitchWorkspace(to);
                return;
            }

            if (to == null || to.IsActive)
                return;

            // Close the other windows first (their repos belong to the old workspace).
            KeepWorkspaceOnClose = true;
            foreach (var other in _all.Skip(1).ToArray())
                other.CloseWindowRequested?.Invoke();
            KeepWorkspaceOnClose = false;
            _current = null;

            _ignoreIndexChange = true;

            var pref = Preferences.Instance;
            foreach (var w in pref.Workspaces)
                w.IsActive = false;

            ActiveWorkspace = to;
            to.IsActive = true;

            foreach (var one in Pages)
                CloseRepositoryInTab(one, false);

            Pages.Clear();
            AddNewTab();

            var repos = to.Repositories.ToArray();
            foreach (var repo in repos)
                OpenRepositoryInTab(repo, null);

            var activeIdx = to.ActiveIdx;
            if (activeIdx >= 0 && activeIdx < Pages.Count)
                ActivePage = Pages[activeIdx];
            else
                ActivePage = Pages[0];

            _ignoreIndexChange = false;
            PostActivePageChanged();
            Preferences.Instance.Save();
            GC.Collect();
        }

        public void AddNewTab()
        {
            var page = new LauncherPage();
            Pages.Add(page);
            ActivePage = page;
        }

        public void MoveTab(LauncherPage from, LauncherPage to)
        {
            _ignoreIndexChange = true;

            var fromIdx = Pages.IndexOf(from);
            var toIdx = Pages.IndexOf(to);
            Pages.Move(fromIdx, toIdx);

            SyncWorkspace();

            _ignoreIndexChange = false;
            ActivePage = from;
        }

        public void GotoNextTab()
        {
            if (Pages.Count == 1)
                return;

            var activeIdx = Pages.IndexOf(_activePage);
            var nextIdx = (activeIdx + 1) % Pages.Count;
            ActivePage = Pages[nextIdx];
        }

        public void GotoPrevTab()
        {
            if (Pages.Count == 1)
                return;

            var activeIdx = Pages.IndexOf(_activePage);
            var prevIdx = activeIdx == 0 ? Pages.Count - 1 : activeIdx - 1;
            ActivePage = Pages[prevIdx];
        }

        public void CloseTab(LauncherPage page)
        {
            if (Pages.Count == 1)
            {
                if (!IsPrimary)
                {
                    // Closing the window closes the repository (see `CloseAll`).
                    CloseWindowRequested?.Invoke();
                    return;
                }

                var last = Pages[0];
                if (last.Data is Repository repo)
                {
                    _activeWorkspace.Repositories.Clear();
                    _activeWorkspace.ActiveIdx = 0;

                    if (last.Node.IsUnmanaged)
                        last.Node.SaveMinimalInfo(repo.GitDir);
                    repo.Close();

                    Welcome.Instance.ClearSearchFilter();
                    last.Node = new RepositoryNode() { Id = Guid.NewGuid().ToString() };
                    last.Data = Welcome.Instance;
                    last.Popup?.Cleanup();
                    last.Popup = null;

                    PostActivePageChanged();
                    GC.Collect();
                }
                else
                {
                    App.Quit(0);
                }

                return;
            }

            page ??= _activePage;

            var removeIdx = Pages.IndexOf(page);
            var activeIdx = Pages.IndexOf(_activePage);
            if (removeIdx == activeIdx)
                ActivePage = Pages[removeIdx > 0 ? removeIdx - 1 : removeIdx + 1];

            CloseRepositoryInTab(page);
            Pages.RemoveAt(removeIdx);
            GC.Collect();
        }

        public void CloseOtherTabs()
        {
            if (Pages.Count == 1)
                return;

            _ignoreIndexChange = true;

            var id = ActivePage.Node.Id;
            foreach (var one in Pages)
            {
                if (one.Node.Id != id)
                    CloseRepositoryInTab(one);
            }

            Pages = new AvaloniaList<LauncherPage> { ActivePage };
            OnPropertyChanged(nameof(Pages));

            _activeWorkspace.ActiveIdx = 0;
            _ignoreIndexChange = false;
            GC.Collect();
        }

        public void CloseRightTabs()
        {
            _ignoreIndexChange = true;

            var endIdx = Pages.IndexOf(ActivePage);
            for (var i = Pages.Count - 1; i > endIdx; i--)
            {
                CloseRepositoryInTab(Pages[i]);
                Pages.Remove(Pages[i]);
            }

            _ignoreIndexChange = false;
            GC.Collect();
        }

        public void OpenRepositoryInTab(string repo, LauncherPage page)
        {
            var normalizedPath = repo.Replace('\\', '/').TrimEnd('/');
            var node = Preferences.Instance.FindNode(normalizedPath) ?? new RepositoryNode
            {
                Id = normalizedPath,
                Name = Path.GetFileName(normalizedPath),
                Bookmark = 0,
                IsRepository = true,
                IsUnmanaged = true
            };

            OpenRepositoryInTab(node, null);
        }

        public void OpenRepositoryInTab(RepositoryNode node, LauncherPage page)
        {
            if (FindPage(node.Id, out var existingOwner) is { } existing)
            {
                existingOwner.ActivePage = existing;
                if (existingOwner != this)
                    existingOwner.RequestBringToTop();
                return;
            }

            if (!Directory.Exists(node.Id))
            {
                ActivePage.Notifications.Add(new Models.Notification
                {
                    Group = node.Id,
                    Message = "Repository does NOT exist any more. Please remove it.",
                    IsError = true,
                });
                return;
            }

            var isBare = new Commands.IsBareRepository(node.Id).GetResult();
            var gitDir = isBare ? node.Id : GetRepositoryGitDir(node.Id);
            if (string.IsNullOrEmpty(gitDir))
            {
                ActivePage.Notifications.Add(new Models.Notification
                {
                    Group = node.Id,
                    Message = "Given path is not a valid git repository!",
                    IsError = true,
                });
                return;
            }

            if (node.IsUnmanaged)
                node.LoadMinimalInfo(gitDir);

            var repo = new Repository(isBare, node.Id, gitDir);
            repo.Open();

            if (page == null)
            {
                if (_activePage == null || _activePage.Node.IsRepository)
                {
                    page = new LauncherPage(node, repo);
                    Pages.Add(page);
                }
                else
                {
                    page = _activePage;
                    page.Node = node;
                    page.Data = repo;
                }
            }
            else
            {
                page.Node = node;
                page.Data = repo;
            }

            SyncWorkspace();

            if (_activePage == page)
                PostActivePageChanged();
            else
                ActivePage = page;
        }

        public void OpenSubRepository(LauncherPage ownerPage, string fullpath)
        {
            var normalizedPath = fullpath.Replace('\\', '/').TrimEnd('/');

            // Check if the sub-repository is already open in any of the tabs
            if (FindPage(normalizedPath, out var existingOwner) is { } existing)
            {
                existingOwner.ActivePage = existing;
                if (existingOwner != this)
                    existingOwner.RequestBringToTop();
                return;
            }

            // Make sure the target directory exists
            if (!Directory.Exists(normalizedPath))
            {
                ownerPage.Notifications.Add(new Models.Notification
                {
                    Group = ownerPage.Node.Id,
                    Message = "Repository path does NOT exist. Please check the path.",
                    IsError = true,
                });

                return;
            }

            // Check if the target directory is a valid git repository
            var gitDir = GetRepositoryGitDir(normalizedPath);
            if (string.IsNullOrEmpty(gitDir))
            {
                ownerPage.Notifications.Add(new Models.Notification
                {
                    Group = ownerPage.Node.Id,
                    Message = "Given path is not a valid git repository!",
                    IsError = true,
                });
                return;
            }

            // Get the owner repository's name and extract the pure owner name (without any prefix)
            var ownerName = ownerPage.Node.Name;
            var colonIdx = ownerName.LastIndexOf(':');
            var pureOwnerName = (colonIdx >= 0 && colonIdx < ownerName.Length - 1) ? ownerName.Substring(colonIdx + 1).Trim() : ownerName;

            // Find the sub-repository node in the preferences or create a new one if it doesn't exist
            var node = Preferences.Instance.FindNode(normalizedPath) ?? new RepositoryNode
            {
                Id = normalizedPath,
                Name = $"{pureOwnerName}: {Path.GetFileName(normalizedPath)}",
                Bookmark = 0,
                IsRepository = true,
                IsUnmanaged = true
            };
            node.LoadMinimalInfo(gitDir);

            var repo = new Repository(false, node.Id, gitDir);
            repo.Open();

            var page = new LauncherPage(node, repo);
            var idxOfOwner = Pages.IndexOf(ownerPage);
            Pages.Insert(idxOfOwner + 1, page);
            SyncWorkspace();
            ActivePage = page;
        }

        private static void DispatchNotification(Models.Notification notification)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Invoke(() => DispatchNotification(notification));
                return;
            }

            if (string.IsNullOrEmpty(notification.Group))
            {
                Current?._activePage?.Notifications.Add(notification);
                return;
            }

            foreach (var launcher in _all)
            {
                foreach (var page in launcher.Pages)
                {
                    var id = page.Node.Id.Replace('\\', '/').TrimEnd('/');
                    if (id.Equals(notification.Group, StringComparison.OrdinalIgnoreCase))
                    {
                        page.Notifications.Add(notification);
                        return;
                    }
                }
            }

            Current?._activePage?.Notifications.Add(notification);
        }

        private string GetRepositoryGitDir(string repo)
        {
            var fullpath = Path.Combine(repo, ".git");
            if (Directory.Exists(fullpath))
            {
                if (Directory.Exists(Path.Combine(fullpath, "refs")) &&
                    Directory.Exists(Path.Combine(fullpath, "objects")) &&
                    File.Exists(Path.Combine(fullpath, "HEAD")))
                    return fullpath;

                return null;
            }

            if (File.Exists(fullpath))
            {
                var redirect = File.ReadAllText(fullpath).Trim();
                if (redirect.StartsWith("gitdir: ", StringComparison.Ordinal))
                    redirect = redirect.Substring(8);

                if (!Path.IsPathRooted(redirect))
                    redirect = Path.GetFullPath(Path.Combine(repo, redirect));

                if (Directory.Exists(redirect))
                    return redirect;

                return null;
            }

            return new Commands.QueryGitDir(repo).GetResult();
        }

        private void CloseRepositoryInTab(LauncherPage page, bool removeFromWorkspace = true)
        {
            if (page.Data is Repository repo)
            {
                if (removeFromWorkspace)
                    _activeWorkspace.Repositories.Remove(repo.FullPath);

                if (page.Node.IsUnmanaged)
                    page.Node.SaveMinimalInfo(repo.GitDir);

                repo.Close();
            }

            page.Popup?.Cleanup();
            page.Popup = null;
            page.Data = null;
        }

        private void PostActivePageChanged()
        {
            if (_ignoreIndexChange)
                return;

            if (_activePage is { Data: Repository repo })
                _activeWorkspace.ActiveIdx = _activeWorkspace.Repositories.IndexOf(repo.FullPath);

            CommandPalette = null;
        }

        private static readonly List<Launcher> _all = new();
        private static Launcher _current;

        private Workspace _activeWorkspace;
        private LauncherPage _activePage;
        private bool _ignoreIndexChange;
        private ICommandPalette _commandPalette;
        private Models.Version _newVersion = null;
    }
}
