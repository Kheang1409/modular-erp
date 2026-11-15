using Erp.Modules.Accounting;
using Erp.Modules.Accounting.Contracts;

namespace Erp.UnitTests;

public sealed class PayrollJournalPosterTests
{
    [Fact]
    public async Task PostsBalancedPayrollThroughGeneralLedgerAndReplaysWithoutDuplicatePost()
    {
        var ledger = new FakeLedger();
        var poster = new PayrollJournalPoster(new FakeJournalResolver(), ledger);
        var request = new PayrollJournalRequest(Guid.NewGuid(), "PAY-2026-11", "revision-1",
            new DateOnly(2026, 11, 30), "USD", "payroll-post-key",
            [new(Guid.NewGuid(), "Salary expense", 1000, 0), new(Guid.NewGuid(), "Tax payable", 0, 100),
             new(Guid.NewGuid(), "Net payable", 0, 900)]);

        var first = await poster.PostPayrollAsync(request, CancellationToken.None);
        var replay = await poster.PostPayrollAsync(request, CancellationToken.None);

        Assert.Equal("Posted", first.State);
        Assert.Equal(first.JournalEntryId, replay.JournalEntryId);
        Assert.Equal(2, ledger.CreateCount);
        Assert.Equal(1, ledger.PostCount);
        Assert.Equal("payroll-post-key", ledger.LastPostingKey);
        Assert.Equal(1000m, ledger.LastCreateRequest!.Lines!.Sum(x => x.Debit));
        Assert.Equal(1000m, ledger.LastCreateRequest!.Lines!.Sum(x => x.Credit));
    }

    [Fact]
    public async Task PayrollReversalUsesAccountingReversalWorkflowAndPreservesReasonAndKey()
    {
        var ledger = new FakeLedger();
        var poster = new PayrollJournalPoster(new FakeJournalResolver(), ledger);
        var originalId = Guid.NewGuid();
        var date = new DateOnly(2026, 11, 10);

        var result = await poster.ReversePayrollAsync(originalId, date, "Incorrect payroll total", "reverse-1",
            CancellationToken.None);

        Assert.Equal("Posted", result.State);
        Assert.Equal(originalId, ledger.ReversedEntryId);
        Assert.Equal(date, ledger.ReversalRequest!.BusinessDate);
        Assert.Equal("Incorrect payroll total", ledger.ReversalRequest.Description);
        Assert.Equal("reverse-1", ledger.ReversalRequest.IdempotencyKey);
    }

    private sealed class FakeLedger : IGeneralLedgerService
    {
        private readonly Guid _entryId = Guid.NewGuid();
        private JournalEntryDto? _entry;
        public int CreateCount { get; private set; }
        public int PostCount { get; private set; }
        public string? LastPostingKey { get; private set; }
        public JournalEntryRequest? LastCreateRequest { get; private set; }
        public Guid? ReversedEntryId { get; private set; }
        public ReverseJournalEntryRequest? ReversalRequest { get; private set; }
        public Task<IReadOnlyList<JournalEntryDto>> ListAsync(CancellationToken token) => throw new NotImplementedException();
        public Task<JournalEntryDto> GetAsync(Guid id, CancellationToken token) => throw new NotImplementedException();
        public Task<JournalEntryDto> ReverseAsync(Guid id, ReverseJournalEntryRequest request, CancellationToken token)
        {
            ReversedEntryId = id;
            ReversalRequest = request;
            return Task.FromResult(new JournalEntryDto(Guid.NewGuid(), "RV-TEST", Guid.NewGuid(), request.BusinessDate,
                request.Description!, "USD", 1, JournalEntryState.Posted, id, null, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, "stamp", []));
        }

        public Task<JournalEntryDto> CreateAsync(JournalEntryRequest request, CancellationToken token)
        {
            CreateCount++;
            LastCreateRequest = request;
            _entry ??= new JournalEntryDto(_entryId, "JE-TEST", request.JournalId, request.BusinessDate,
                request.Description!, request.CurrencyCode!, request.ExchangeRate, JournalEntryState.Draft,
                null, null, DateTimeOffset.UtcNow, null, "stamp", []);
            return Task.FromResult(_entry);
        }

        public Task<JournalEntryDto> PostAsync(Guid id, PostJournalEntryRequest request, CancellationToken token)
        {
            PostCount++;
            LastPostingKey = request.IdempotencyKey;
            _entry = _entry! with { State = JournalEntryState.Posted, PostedAtUtc = DateTimeOffset.UtcNow };
            return Task.FromResult(_entry);
        }
    }

    private sealed class FakeJournalResolver : IPayrollJournalResolver
    {
        public Task<Guid?> FindActivePayrollJournalAsync(CancellationToken cancellationToken) => Task.FromResult<Guid?>(Guid.NewGuid());
    }
}
