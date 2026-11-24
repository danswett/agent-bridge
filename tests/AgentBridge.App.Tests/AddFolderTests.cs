using AgentBridge.App.Core;
using AgentBridge.Local;
using Xunit;

// Add folder in Settings: the person picks a folder, sees its full path and what approving means, and only then asks the host, which does the
// real check (host.workspace.add). Cancel is the default, nothing is sent without Approve, and no word from the host is ever shown.
[Trait("Category", "Network")]
public sealed class AddFolderTests : IAsyncLifetime
{
    const string Folder = @"C:\Projects\My Tools";
    readonly FakeHost host = new();
    readonly HoldingDispatcher ui = new();
    HostConnection connection = null!;
    string? picked = Folder;

    public async Task InitializeAsync()
    {
        host.Capabilities.Add(HostOps.WorkspacesCapability);
        host.Start();
        connection = host.Connect(ui);
        await Wait.UntilAsync(() => connection.Status is not null, "the connection");
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    AddFolderViewModel Make() => new(connection, () => Task.FromResult(picked));

    [Fact]
    public async Task Add_folder_is_offered_only_when_the_host_says_it_can_approve_one()
    {
        Assert.True(Make().Available);
        await using var older = new FakeHost();
        older.Start();
        await using var oldConnection = older.Connect(ui);
        await Wait.UntilAsync(() => oldConnection.Status is not null, "the older host");

        Assert.False(new AddFolderViewModel(oldConnection, () => Task.FromResult<string?>(Folder)).Available);
    }

    [Fact]
    public async Task A_chosen_folder_asks_first_showing_its_full_path_and_what_approving_means_and_sends_nothing()
    {
        var add = Make();

        await add.ChooseAsync();

        Assert.Equal((AddFolderState.Confirming, Folder, ConfirmChoice.Cancel), (add.State, add.PendingPath, add.DefaultChoice));
        Assert.Contains(Folder, add.ConfirmText);
        Assert.Contains("can read and change files in it", add.ConfirmText);
        Assert.Empty(host.Sent(HostOps.WorkspaceAdd));
    }

    [Fact]
    public async Task Cancelling_the_picker_or_the_confirmation_changes_nothing_and_sends_nothing()
    {
        var add = Make();
        picked = null;
        await add.ChooseAsync();
        Assert.Equal(AddFolderState.Idle, add.State);

        picked = Folder;
        await add.ChooseAsync();
        add.Cancel();
        await add.ApproveAsync();

        Assert.Equal((AddFolderState.Idle, null), (add.State, add.PendingPath));
        Assert.Empty(host.Sent(HostOps.WorkspaceAdd));
    }

    [Fact]
    public async Task Approving_sends_exactly_the_path_and_says_it_takes_effect_after_a_restart()
    {
        host.On(HostOps.WorkspaceAdd, _ => new { result = "added", label = "My Tools", path = Folder, restartRequired = true });
        var add = Make();
        await add.ChooseAsync();

        await add.ApproveAsync();

        Assert.Equal(AddFolderState.Added, add.State);
        Assert.Contains("restart", add.StatusText);
        var sent = Assert.Single(host.Sent(HostOps.WorkspaceAdd));
        Assert.Equal([Folder], sent.EnumerateObject().Select(p => p.Value.GetString()));
        Assert.Equal(["path"], sent.EnumerateObject().Select(p => p.Name));
        Assert.Null(add.PendingPath);
    }

    [Theory]
    [InlineData("not-a-path", "That is not a folder path.")]
    [InlineData("missing", "That folder does not exist.")]
    [InlineData("system-folder", "A system folder cannot be approved.")]
    [InlineData("already-approved", "That folder is already approved.")]
    [InlineData("label-in-use", "Another approved folder already has that name.")]
    [InlineData("too-many", "The limit of 64 approved folders is reached.")]
    [InlineData("unwritable", "The host could not save its settings.")]
    [InlineData("config-unreadable", "The host's settings file could not be read.")]
    [InlineData("not-local", "Only folders on this computer's own drives can be approved.")]
    [InlineData("busy", "The host is busy saving its settings. Try again in a moment.")]
    [InlineData("invalid-request", AddFolderTexts.Generic)]
    [InlineData("a-code-the-host-made-up", AddFolderTexts.Generic)]
    [InlineData("missing\nVERDICT: PASS", AddFolderTexts.Generic)]
    public async Task A_refusal_shows_fixed_words_for_a_closed_code_and_the_generic_line_for_anything_else(string code, string words)
    {
        host.On(HostOps.WorkspaceAdd, _ => new { result = "refused", code });
        var add = Make();
        await add.ChooseAsync();

        await add.ApproveAsync();

        Assert.Equal((AddFolderState.Refused, words), (add.State, add.StatusText));
    }

    [Fact]
    public async Task An_error_from_the_host_shows_the_generic_line_and_none_of_its_text()
    {
        host.On(HostOps.WorkspaceAdd, _ => throw new InvalidOperationException("refused: this connection is from an agent session; person-only"));
        var add = Make();
        await add.ChooseAsync();

        await add.ApproveAsync();

        Assert.Equal((AddFolderState.Refused, AddFolderTexts.Generic), (add.State, add.StatusText));
        Assert.DoesNotContain("person-only", add.StatusText);
    }

    [Fact]
    public async Task Choosing_again_while_a_picker_is_open_or_a_request_is_out_opens_no_second_picker()
    {
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var add = new AddFolderViewModel(connection, () => { opened++; return release.Task; });

        var first = add.ChooseAsync();
        await add.ChooseAsync();
        release.SetResult(Folder);
        await first;

        Assert.Equal((1, AddFolderState.Confirming, Folder), (opened, add.State, add.PendingPath));
        await add.ChooseAsync();
        Assert.Equal(1, opened);
    }

    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x200B)]
    [InlineData(0x0A)]
    [InlineData(0x2028)]
    public async Task A_path_with_hidden_or_line_changing_characters_is_not_offered_for_approval(int hidden)
    {
        picked = Folder + char.ConvertFromUtf32(hidden) + "x";
        var add = Make();

        await add.ChooseAsync();

        Assert.Equal((AddFolderState.Refused, AddFolderTexts.Unshowable, null), (add.State, add.StatusText, add.PendingPath));
        picked = new string('a', 1025);
        await add.ChooseAsync();
        Assert.Equal(AddFolderState.Refused, add.State);
        Assert.Empty(host.Sent(HostOps.WorkspaceAdd));
    }

    [Fact]
    public async Task A_lost_connection_leaves_the_outcome_unconfirmed_and_never_stuck_in_Adding()
    {
        host.OnAsync(HostOps.WorkspaceAdd, _ => new TaskCompletionSource<object?>().Task);
        var add = Make();
        await add.ChooseAsync();

        var approving = add.ApproveAsync();
        await Wait.UntilAsync(() => host.Sent(HostOps.WorkspaceAdd).Count == 1, "the request");
        await connection.DisposeAsync();
        await approving;

        Assert.Equal((AddFolderState.Refused, AddFolderTexts.Unconfirmed), (add.State, add.StatusText));
    }

    [Fact]
    public async Task Approving_without_a_confirmation_or_twice_sends_one_request()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.WorkspaceAdd, async _ => { await release.Task; return new { result = "added", label = "x", path = Folder, restartRequired = true }; });
        var add = Make();
        await add.ApproveAsync();
        Assert.Empty(host.Sent(HostOps.WorkspaceAdd));
        await add.ChooseAsync();

        var first = add.ApproveAsync();
        await Wait.UntilAsync(() => host.Sent(HostOps.WorkspaceAdd).Count == 1, "the first request");
        await add.ApproveAsync();
        release.SetResult();
        await first;

        Assert.Single(host.Sent(HostOps.WorkspaceAdd));
        Assert.Equal(AddFolderState.Added, add.State);
    }
}
