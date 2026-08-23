using QuickPhrase.Core;
using QuickPhrase.Desktop;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class PhraseLibraryDeliveryViewModelTests
{
    [Fact]
    public async Task InsertCommand_UsesInsertOnlyAndSendCommandUsesInsertAndSend()
    {
        var delivery = new FakeLibraryDeliveryCoordinator();
        var vm = new PhraseLibraryViewModel(new FakeCommandService(), delivery);
        var item = new PhraseItemViewModel(CreatePhrase(), null) { Owner = vm };

        await vm.InsertCommand.ExecuteAsync(item);
        await vm.SendCommand.ExecuteAsync(item);

        Assert.Equal([SendMode.InsertOnly, SendMode.InsertAndSend], delivery.Modes);
    }

    [Fact]
    public void AvailabilityChange_RefreshesRowSendStateAndChineseDisabledReason()
    {
        var delivery = new FakeLibraryDeliveryCoordinator { Availability = new(false, false, "不能插入", "当前目标不能发送") };
        var vm = new PhraseLibraryViewModel(new FakeCommandService(), delivery);
        var item = new PhraseItemViewModel(CreatePhrase(), null) { Owner = vm };

        delivery.RaiseAvailabilityChanged();

        Assert.False(item.CanSend);
        Assert.Equal("当前目标不能发送", item.SendDisabledReason);
    }

    [Fact]
    public void AvailabilityChange_RefreshesSeparateMainAndSearchResultInstancesOfSamePhrase()
    {
        var delivery = new FakeLibraryDeliveryCoordinator { Availability = new(true, true, "", "") };
        var vm = new PhraseLibraryViewModel(new FakeCommandService(), delivery);
        var phrase = CreatePhrase();
        var mainItem = new PhraseItemViewModel(phrase, null) { Owner = vm };
        var searchItem = new PhraseItemViewModel(phrase, null) { Owner = vm };
        vm.Phrases.Add(mainItem);
        vm.SearchResults.Add(searchItem);
        delivery.Availability = new(false, false, "目标已失效", "目标已失效");

        delivery.RaiseAvailabilityChanged();

        Assert.False(mainItem.CanSend);
        Assert.False(searchItem.CanSend);
    }
    [Fact]
    public void ApplyPhrase_RefreshesAvailabilityWhenContentChangesToImage()
    {
        var delivery = new FakeLibraryDeliveryCoordinator
        {
            EvaluateHandler = phrase => phrase.Body.ImageCount == 0
                ? new(true, true, "", "")
                : new(false, false, "图片不可插入", "图片不可发送"),
        };
        var vm = new PhraseLibraryViewModel(new FakeCommandService(), delivery);
        var item = new PhraseItemViewModel(CreatePhrase(), null) { Owner = vm };
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 10, 1, 1);

        item.Apply(CreatePhrase() with { Body = new PhraseBody([PhraseSegment.CreateImage(image)]) }, null);

        Assert.False(item.CanSend);
        Assert.Equal("图片不可发送", item.SendDisabledReason);
    }

    [Fact]
    public void Dispose_UnsubscribesFromCoordinatorAvailabilityChanges()
    {
        var delivery = new FakeLibraryDeliveryCoordinator();
        var vm = new PhraseLibraryViewModel(new FakeCommandService(), delivery);
        _ = new PhraseItemViewModel(CreatePhrase(), null) { Owner = vm };
        var beforeDispose = delivery.EvaluateCalls;

        vm.Dispose();
        delivery.RaiseAvailabilityChanged();

        Assert.Equal(beforeDispose, delivery.EvaluateCalls);
    }

    private static Phrase CreatePhrase() => new(
        Guid.NewGuid(), "欢迎语", PhraseBody.FromText("您好"), Guid.NewGuid(), ShortcutMode.None, null,
        0, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FakeLibraryDeliveryCoordinator : ILibraryDeliveryCoordinator
    {
        public event EventHandler? AvailabilityChanged;
        public LibraryDeliveryAvailability Availability { get; set; } = new(true, true, "", "");
        public Func<Phrase, LibraryDeliveryAvailability>? EvaluateHandler { get; set; }
        public List<SendMode> Modes { get; } = [];
        public int EvaluateCalls { get; private set; }
        public LibraryDeliveryAvailability Evaluate(Phrase phrase) { EvaluateCalls++; return EvaluateHandler?.Invoke(phrase) ?? Availability; }
        public Task<DeliveryResult?> DeliverAsync(Phrase phrase, SendMode mode, CancellationToken cancellationToken)
        {
            Modes.Add(mode);
            return Task.FromResult<DeliveryResult?>(null);
        }
        public void RaiseAvailabilityChanged() => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
