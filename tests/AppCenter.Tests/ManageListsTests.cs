using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// What the Manage page holds and says, without the page: which rows the
/// filter leaves, in what order, what the headings and empty cards say, what
/// "update all" asks and works through, and what is left to say above the
/// lists once an operation has ended.
/// </summary>
public class ManageListsTests
{
    private static readonly Dictionary<string, CatalogEntry> NoCatalog = new(StringComparer.OrdinalIgnoreCase);

    private static AppPackage Update(string id, string name, string from = "1.0", string to = "2.0") => new()
    {
        Id = id,
        Name = name,
        Version = from,
        AvailableVersion = to,
        IsInstalled = true,
        ClosesApp = SelfPackages.Includes(id),
    };

    private static AppPackage Install(string id, string name, string version = "1.0", bool system = false) => new()
    {
        Id = id,
        Name = name,
        Version = version,
        IsInstalled = true,
        IsSystemPackage = system,
    };

    private static ManageLists Loaded(
        IEnumerable<AppPackage> updates,
        IEnumerable<AppPackage> installed,
        string needle = "",
        bool showSystem = false,
        bool descending = false)
    {
        var lists = new ManageLists();
        lists.Load(updates, installed, NoCatalog);
        lists.Filter(needle, showSystem, descending);
        return lists;
    }

    private static Operation Single(string key, OperationKind kind = OperationKind.Update) => new()
    {
        Key = key,
        PackageName = key,
        Kind = kind,
    };

    private static Operation Batch() => new()
    {
        Key = Operation.UpdateAllKey,
        PackageName = "all packages",
        Kind = OperationKind.UpdateAll,
    };

    // -----------------------------------------------------------------
    // Loading and filtering
    // -----------------------------------------------------------------

    [Fact]
    public void Fills_in_what_the_catalogue_knows_about_a_package()
    {
        var catalog = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Git.Git"] = new() { Id = "Git.Git", Publisher = "The Git Development Community", Homepage = "https://git-scm.com" },
        };

        var lists = new ManageLists();
        lists.Load([Update("Git.Git", "Git")], [], catalog);

        Assert.Equal("The Git Development Community", lists.AllUpdates[0].Publisher);
        Assert.Equal("https://git-scm.com", lists.AllUpdates[0].Homepage);
    }

    [Fact]
    public void Finds_a_package_by_name_or_id_in_both_lists()
    {
        var lists = Loaded(
            [Update("Git.Git", "Git"), Update("7zip.7zip", "7-Zip")],
            [Install("Git.Git", "Git"), Install("Mozilla.Firefox", "Firefox")],
            needle: "  git ");

        Assert.Equal(["Git.Git"], lists.Updates.Select(p => p.Id));
        Assert.Equal(["Git"], lists.Installed.Select(g => g.Title));

        lists.Filter("mozilla", showSystem: false, descending: false);

        Assert.Empty(lists.Updates);
        Assert.Equal(["Firefox"], lists.Installed.Select(g => g.Title));
    }

    [Fact]
    public void Hides_system_packages_from_the_installs_but_not_from_the_updates()
    {
        var lists = Loaded(
            [Update("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64)")],
            [Install("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", system: true)]);

        // An update waiting on a system package is exactly the kind worth seeing.
        Assert.Single(lists.Updates);
        Assert.Empty(lists.Installed);

        lists.Filter(string.Empty, showSystem: true, descending: false);

        Assert.Single(lists.Installed);
    }

    [Fact]
    public void Keeps_what_closes_the_app_last_whichever_way_the_list_is_sorted()
    {
        AppPackage[] updates =
        [
            Update(AppInfo.PackageId, "App Center"),
            Update("Git.Git", "Git"),
            Update("Zoom.Zoom", "Zoom"),
        ];

        // The order shown is the order "update all" runs in, and App Center
        // replacing itself has to be the last thing it does.
        var ascending = Loaded(updates, []);
        Assert.Equal(["Git", "Zoom", "App Center"], ascending.Updates.Select(p => p.Name));

        ascending.Filter(string.Empty, showSystem: false, descending: true);
        Assert.Equal(["Zoom", "Git", "App Center"], ascending.Updates.Select(p => p.Name));
    }

    [Fact]
    public void Remembers_which_families_were_open_when_the_rows_are_rebuilt()
    {
        var lists = Loaded([],
        [
            Install("Microsoft.DotNet.SDK.9", "Microsoft .NET SDK 9.0.317 (x64)", "9.0.317"),
            Install("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.201 (x64)", "10.0.201"),
        ]);

        var sdk = Assert.Single(lists.Installed);
        sdk.IsExpanded = true;

        lists.Filter("sdk", showSystem: false, descending: false);

        var rebuilt = Assert.Single(lists.Installed);
        Assert.NotSame(sdk, rebuilt);
        Assert.True(rebuilt.IsExpanded);
    }

    [Fact]
    public void Says_when_a_suite_is_opened_so_its_icons_can_be_fetched()
    {
        var lists = Loaded([],
        [
            Install("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219"),
            Install("Microsoft.WindowsAppRuntime.1.6", "Microsoft Windows App Runtime 1.6", "6000.311.13.0"),
        ]);

        var opened = new List<InstalledGroup>();
        lists.SuiteOpened += (_, suite) => opened.Add(suite);

        var suite = Assert.Single(lists.Installed);
        Assert.True(suite.IsSuite);

        suite.IsExpanded = true;

        Assert.Same(suite, Assert.Single(opened));
    }

    // -----------------------------------------------------------------
    // What the page says
    // -----------------------------------------------------------------

    [Fact]
    public void Counts_every_update_whatever_the_filter_is_showing()
    {
        var lists = Loaded([Update("Git.Git", "Git"), Update("7zip.7zip", "7-Zip")], [], needle: "firefox");

        // "Update all" means all of them, so the heading and the button say so.
        Assert.Equal("Updates available (2)", lists.UpdatesHeading);
        Assert.Equal("Update all (2)", lists.UpdateAllLabel);
        Assert.Equal("None of the 2 updates match “firefox”.", lists.UpdatesEmptyText(wingetAvailable: true));
    }

    [Fact]
    public void Counts_the_installed_apps_the_system_toggle_lets_through_whatever_the_filter_is_showing()
    {
        IEnumerable<AppPackage> installs =
        [
            Install("Git.Git", "Git"),
            Install("7zip.7zip", "7-Zip", "22.01"),
            Install("7zip.7zip", "7-Zip", "26.02"),
            Install("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010", system: true),
        ];

        var filtered = Loaded([], installs, needle: "git");
        Assert.Equal("Installed apps (3)", filtered.InstalledHeading);
        Assert.Equal("Installed apps (4)", Loaded([], installs, showSystem: true).InstalledHeading);

        // The status line measures against the same count, not every install.
        Assert.Equal("Showing 1 of 3 packages (2 hidden by the current filter).", filtered.InstalledStatus);
    }

    [Fact]
    public void Says_why_there_are_no_updates()
    {
        var lists = Loaded([], []);

        Assert.Equal("Update all", lists.UpdateAllLabel);
        Assert.Equal("Everything is up to date.", lists.UpdatesEmptyText(wingetAvailable: true));
        Assert.StartsWith("winget could not be started.", lists.UpdatesEmptyText(wingetAvailable: false));
    }

    [Fact]
    public void Says_why_the_installed_list_is_empty()
    {
        Assert.Equal("winget lists nothing as installed.", Loaded([], []).InstalledEmptyText);

        var onlySystem = Loaded([], [Install("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010", system: true)]);
        Assert.StartsWith("Every installed package is a system package.", onlySystem.InstalledEmptyText);

        var noMatch = Loaded([], [Install("Git.Git", "Git")], needle: "zzz");
        Assert.Equal("No installed apps match “zzz”. System packages are hidden.", noMatch.InstalledEmptyText);
    }

    [Fact]
    public void Counts_what_the_filter_hides_and_the_families_in_several_versions()
    {
        var lists = Loaded([],
        [
            Install("Microsoft.DotNet.SDK.9", "Microsoft .NET SDK 9.0.317 (x64)", "9.0.317"),
            Install("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.201 (x64)", "10.0.201"),
            Install("Git.Git", "Git"),
        ]);

        Assert.Equal("Showing 3 packages, with 1 installed in several versions.", lists.InstalledStatus);

        lists.Filter("git", showSystem: false, descending: false);

        Assert.Equal("Showing 1 of 3 packages (2 hidden by the current filter).", lists.InstalledStatus);
    }

    // -----------------------------------------------------------------
    // Questions
    // -----------------------------------------------------------------

    [Fact]
    public void Update_all_names_five_counts_the_rest_and_names_what_closes_the_app()
    {
        var lists = Loaded(
        [
            .. Enumerable.Range(1, 6).Select(i => Update($"Vendor.App{i}", $"App {i}")),
            Update(AppInfo.PackageId, "App Center"),
        ], []);

        var question = lists.UpdateAllQuestion();

        Assert.Equal("Update 7 packages?", question.Title);
        Assert.Contains("App 1, App 2, App 3, App 4, App 5, and 2 more.", question.Message);
        Assert.Contains("App Center is left until last.", question.Message);
        Assert.Equal("Update all", question.Confirm);

        // The batch is every update, in the order the rows are shown.
        Assert.Equal(AppInfo.PackageId, lists.UpdateAllBatch()[^1].Id);
        Assert.Equal(7, lists.UpdateAllBatch().Count);
    }

    [Fact]
    public void Uninstalling_one_of_several_versions_says_the_others_stay()
    {
        var older = Install("7zip.7zip", "7-Zip", "22.01");
        older.IsOneOfSeveralVersions = true;

        var question = ManageLists.UninstallQuestion(older);

        Assert.Equal("Uninstall 7-Zip 22.01?", question.Title);
        Assert.Contains("Other versions of it stay installed.", question.Message);
    }

    // -----------------------------------------------------------------
    // Operations
    // -----------------------------------------------------------------

    [Fact]
    public void Update_all_takes_off_the_rows_it_updated_and_leaves_the_ones_it_could_not()
    {
        var lists = Loaded([Update("Git.Git", "Git"), Update("7zip.7zip", "7-Zip")], []);
        var batch = Batch();

        batch.BeginItem("Git.Git", "Git");
        batch.EndItem("Git.Git", string.Empty);
        batch.BeginItem("7zip.7zip", "7-Zip");
        batch.EndItem("7zip.7zip", "The installer hit a fatal error. (1603)");

        Assert.True(lists.DropUpdated(batch));
        Assert.Equal(["7zip.7zip"], lists.Updates.Select(p => p.Id));
        Assert.Equal(["7zip.7zip"], lists.AllUpdates.Select(p => p.Id));

        // Nothing more to take off: says so, so the page can skip its repaint.
        Assert.False(lists.DropUpdated(batch));
    }

    [Fact]
    public void Leaves_a_failure_to_its_row_when_the_row_can_be_seen()
    {
        var git = Update("Git.Git", "Git");
        var lists = Loaded([git], []);

        var operation = Single("Git.Git");
        operation.Complete(new WingetResult(1603, string.Empty, string.Empty), null);
        git.Error = operation.Summary;

        // Already said in red on the row: saying it again above reads as a glitch.
        Assert.Null(lists.Unexplained(operation));

        // Hidden by the filter, the row explains nothing to anybody.
        lists.Filter("zzz", showSystem: false, descending: false);
        Assert.Equal(operation.Summary, lists.Unexplained(operation));
    }

    [Fact]
    public void Says_how_an_operation_that_went_fine_ended()
    {
        var operation = Single("Git.Git");
        operation.Report("Successfully installed");
        operation.Complete(new WingetResult(0, string.Empty, string.Empty), null);

        Assert.Equal(operation.Summary, Loaded([], []).Unexplained(operation));
        Assert.Null(Loaded([], []).Unexplained(null));
    }

    [Fact]
    public void Says_which_restart_an_update_that_went_through_is_waiting_on()
    {
        var teams = Update("Microsoft.Teams", "Microsoft Teams");
        var driver = Update("Vendor.Driver", "Some Driver");
        var lists = Loaded([teams, driver], []);

        var app = Single("Microsoft.Teams");
        app.Complete(new WingetResult(0, string.Empty, string.Empty), null);
        lists.NoteUnfinishedUpdate(app);

        var windows = Single("Vendor.Driver");
        windows.Complete(new WingetResult(unchecked((int)0x8A150109), string.Empty, string.Empty), null);
        lists.NoteUnfinishedUpdate(windows);

        Assert.Equal("Restart the app to finish", teams.Status);
        Assert.Equal("Restart Windows to finish", driver.Status);
    }

    [Fact]
    public void Says_nothing_of_a_restart_for_an_update_that_failed()
    {
        var git = Update("Git.Git", "Git");
        var lists = Loaded([git], []);

        var operation = Single("Git.Git");
        operation.Complete(new WingetResult(1603, string.Empty, string.Empty), null);
        lists.NoteUnfinishedUpdate(operation);

        Assert.Equal(string.Empty, git.Status);
    }

    [Fact]
    public void Opens_a_family_whose_member_is_busy()
    {
        var sdk9 = Install("Microsoft.DotNet.SDK.9", "Microsoft .NET SDK 9.0.317 (x64)", "9.0.317");
        var lists = Loaded([],
        [
            sdk9,
            Install("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.201 (x64)", "10.0.201"),
        ]);

        var family = Assert.Single(lists.Installed);
        Assert.False(family.IsExpanded);

        // A bar inside a closed family is a bar nobody is looking at.
        sdk9.IsBusy = true;
        lists.OpenFamiliesWithNews();

        Assert.True(family.IsExpanded);
    }

    [Fact]
    public void Lists_every_row_with_the_action_its_button_offers()
    {
        var lists = Loaded([Update("Git.Git", "Git")], [Install("Git.Git", "Git"), Install("7zip.7zip", "7-Zip")]);

        Assert.Equal(
            [OperationKind.Update, OperationKind.Uninstall],
            lists.RowsFor("git.git").Select(r => r.Shows));
        Assert.Equal(3, lists.Rows().Count());
    }

    // -----------------------------------------------------------------
    // Three kinds of update, three lists
    // -----------------------------------------------------------------

    private static AppPackage Unknown(string id, string name, string to = "2.0") => Update(id, name, from: "Unknown", to: to);

    private static AppPackage Pinned(string id, string name)
    {
        var package = Update(id, name);
        package.IsPinned = true;
        package.PinKind = "Pinning";
        return package;
    }

    [Fact]
    public void Sorts_each_update_into_the_list_its_kind_belongs_in()
    {
        var lists = Loaded(
            [Update("Git.Git", "Git"), Unknown("Vendor.Tool", "Tool"), Pinned("Microsoft.PowerToys", "PowerToys")],
            []);

        Assert.Equal(["Git.Git"], lists.Updates.Select(p => p.Id));
        Assert.Equal(["Vendor.Tool"], lists.UnknownUpdates.Select(p => p.Id));
        Assert.Equal(["Microsoft.PowerToys"], lists.SkippedUpdates.Select(p => p.Id));
    }

    [Fact]
    public void Counts_only_the_updates_winget_is_sure_of_in_the_heading_and_the_button()
    {
        var lists = Loaded(
            [Update("Git.Git", "Git"), Unknown("Vendor.Tool", "Tool"), Pinned("Microsoft.PowerToys", "PowerToys")],
            []);

        // A package that may be up to date already, and one being left alone
        // on purpose, are not numbers to put on a button that says "all".
        Assert.Equal("Updates available (1)", lists.UpdatesHeading);
        Assert.Equal("Update all (1)", lists.UpdateAllLabel);
        Assert.Equal("Version unknown (1)", lists.UnknownHeading);
        Assert.Equal("Skipped updates (1)", lists.SkippedHeading);
        Assert.Equal(["Git.Git"], lists.UpdateAllBatch().Select(b => b.Id));
    }

    [Fact]
    public void Gives_the_unknown_list_a_batch_of_its_own_that_leaves_out_what_is_already_installed()
    {
        var done = Unknown("Vendor.Done", "Done");
        done.RecordedInstall = new RecordedInstall("2.0", DateTime.Now);

        var lists = Loaded([Unknown("Vendor.Tool", "Tool"), done, Update("Git.Git", "Git")], []);

        Assert.Equal(["Vendor.Tool"], lists.UnknownBatch().Select(b => b.Id));
        Assert.Equal("Update all of these (1)", lists.UpdateUnknownLabel);

        var question = lists.UpdateUnknownQuestion();
        Assert.Equal("Install the newest version of 1 package?", question.Title);
        Assert.Contains("cannot read what version is installed of: Tool.", question.Message);
        Assert.Equal("Install newest", question.Confirm);
    }

    [Fact]
    public void Finds_a_package_in_whichever_list_it_is_in()
    {
        var lists = Loaded(
            [Update("Git.Git", "Git"), Unknown("Vendor.Tool", "Tool"), Pinned("Microsoft.PowerToys", "PowerToys")],
            [],
            needle: "power");

        Assert.Empty(lists.Updates);
        Assert.Empty(lists.UnknownUpdates);
        Assert.Equal(["Microsoft.PowerToys"], lists.SkippedUpdates.Select(p => p.Id));
    }

    [Fact]
    public void Says_that_the_other_lists_have_the_rest_rather_than_that_all_is_up_to_date()
    {
        Assert.Equal("Everything is up to date.", Loaded([], []).UpdatesEmptyText(wingetAvailable: true));

        var others = Loaded([Unknown("Vendor.Tool", "Tool")], []);
        Assert.Equal("Nothing winget is sure needs updating. The lists below have the rest.", others.UpdatesEmptyText(wingetAvailable: true));
    }

    [Fact]
    public void Asks_a_different_question_of_a_package_whose_version_winget_cannot_read()
    {
        var question = ManageLists.UpdateQuestion(Unknown("Vendor.Tool", "Tool"));

        Assert.Equal("Update Tool?", question.Title);
        Assert.Contains("cannot read which version of Tool is installed", question.Message);
        Assert.Contains("It will install 2.0 over what is there.", question.Message);

        var done = Unknown("Vendor.Tool", "Tool");
        done.RecordedInstall = new RecordedInstall("2.0", new DateTime(2026, 9, 26));

        var again = ManageLists.UpdateQuestion(done);
        Assert.Equal("Reinstall Tool?", again.Title);
        Assert.Contains("App Center installed 2.0 here on 26 September already.", again.Message);
        Assert.Equal("Reinstall", again.Confirm);
    }

    [Fact]
    public void Asks_before_a_reinstall_and_says_what_it_costs()
    {
        var question = ManageLists.ReinstallQuestion(Update("Git.Git", "Git", from: "2.47", to: "2.55"));

        Assert.Equal("Reinstall Git to update it?", question.Title);
        Assert.Contains("uninstall the installed 2.47, then install 2.55 afresh", question.Message);
        Assert.Contains("anything the uninstaller removes does not", question.Message);
        Assert.Equal("Reinstall", question.Confirm);
    }

    [Fact]
    public void Writes_down_what_went_in_over_a_version_winget_cannot_read()
    {
        UpdateMemory.UseScratch();
        UpdateMemory.Now = () => new DateTime(2026, 9, 26);

        var tool = Unknown("Vendor.Tool", "Tool");
        var git = Update("Git.Git", "Git");
        var lists = Loaded([tool, git], []);

        var operation = Single("Vendor.Tool");
        operation.Complete(new WingetResult(0, string.Empty, string.Empty), null);
        lists.RememberInstalls(operation);

        // The row says so at once, and the next read of the machine says it again.
        Assert.True(tool.IsRecordedAsCurrent);
        Assert.Equal("Reinstall", tool.ActionLabel);
        Assert.Equal(new RecordedInstall("2.0", new DateTime(2026, 9, 26)), UpdateMemory.Recorded("vendor.tool"));

        // A package winget can read the version of needs no memory.
        Assert.Null(UpdateMemory.Recorded("Git.Git"));
    }

    [Fact]
    public void Remembers_what_a_batch_got_through_as_well()
    {
        UpdateMemory.UseScratch();

        var tool = Unknown("Vendor.Tool", "Tool");
        var lists = Loaded([tool], []);

        var batch = Batch();
        batch.BeginItem("Vendor.Tool", "Tool");
        batch.EndItem("Vendor.Tool", string.Empty);
        lists.RememberInstalls(batch);

        Assert.NotNull(UpdateMemory.Recorded("Vendor.Tool"));
    }

    [Fact]
    public void Remembers_nothing_of_an_update_that_failed()
    {
        UpdateMemory.UseScratch();

        var tool = Unknown("Vendor.Tool", "Tool");
        var lists = Loaded([tool], []);

        var operation = Single("Vendor.Tool");
        operation.Complete(new WingetResult(1603, string.Empty, string.Empty), null);
        lists.RememberInstalls(operation);

        Assert.Null(UpdateMemory.Recorded("Vendor.Tool"));
        Assert.Null(tool.RecordedInstall);
    }

    [Fact]
    public void Paints_the_memory_back_onto_a_fresh_read()
    {
        UpdateMemory.UseScratch();
        UpdateMemory.Record("Vendor.Tool", "2.0");

        var tool = Unknown("Vendor.Tool", "Tool");
        var newer = Unknown("Vendor.Newer", "Newer", to: "3.0");
        var git = Update("Git.Git", "Git");

        UpdateMemory.Apply([tool, newer, git]);

        Assert.True(tool.IsRecordedAsCurrent);
        Assert.Null(newer.RecordedInstall);
        Assert.Null(git.RecordedInstall);
    }

    [Fact]
    public void Says_nothing_of_a_restart_for_a_package_winget_cannot_read_the_version_of()
    {
        var tool = Unknown("Vendor.Tool", "Tool");
        var lists = Loaded([tool], []);

        var operation = Single("Vendor.Tool");
        operation.Complete(new WingetResult(0, string.Empty, string.Empty), null);
        lists.NoteUnfinishedUpdate(operation);

        // It is back in the list whatever happened; "restart the app to finish"
        // would be a guess dressed as an instruction.
        Assert.Equal(string.Empty, tool.Status);
    }

    [Fact]
    public void Update_all_takes_a_finished_row_off_whichever_list_it_was_in()
    {
        var lists = Loaded([Update("Git.Git", "Git"), Unknown("Vendor.Tool", "Tool")], []);
        var batch = Batch();

        batch.BeginItem("Vendor.Tool", "Tool");
        batch.EndItem("Vendor.Tool", string.Empty);

        Assert.True(lists.DropUpdated(batch));
        Assert.Empty(lists.UnknownUpdates);
        Assert.Single(lists.Updates);
    }
}
