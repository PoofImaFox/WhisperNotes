using System.Runtime.CompilerServices;
using NoteScribe.Core.Ai;
using NoteScribe.Core.Configuration;

namespace NoteScribe.App.DesignData;

/// <summary>
/// Canned but plausible answers, so the assistant pane can be designed, clicked through and
/// screenshotted without a model on the machine.
/// </summary>
/// <remarks>
/// It streams word by word behind a short delay on purpose: the streaming path and its "still
/// thinking" affordances are the parts most likely to look wrong, and they only look wrong when
/// text actually arrives in pieces.
/// </remarks>
internal sealed class FakeAiAssistant : IAiAssistant
{
    private const int FirstTokenDelayMs = 350;
    private const int PerWordDelayMs = 18;

    public AiProviderKind Provider => AiProviderKind.Ollama;

    public string ModelId => "llama3.1 (sample data)";

    public bool IsConfigured => true;

    public string? ConfigurationHint => null;

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Task.Delay(FirstTokenDelayMs, cancellationToken).ConfigureAwait(false);

        var text = Answer(request);
        return new AiResult(text, ModelId, EstimateTokens(request), text.Length / 4);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Task.Delay(FirstTokenDelayMs, cancellationToken).ConfigureAwait(false);

        var text = Answer(request);
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            // Break after whitespace so fragments concatenate back to exactly the same string.
            if (!char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                i++;
            }

            yield return text[start..(i + 1)];
            start = i + 1;

            await Task.Delay(PerWordDelayMs, cancellationToken).ConfigureAwait(false);
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> models = ["llama3.1", "llama3.1:70b", "qwen2.5:14b", "mistral-nemo"];
        return Task.FromResult(models);
    }

    private static int EstimateTokens(AiRequest request) =>
        (request.SystemPrompt.Length + request.Messages.Sum(m => m.Text.Length)) / 4;

    /// <summary>Picks the canned answer that matches the action's system prompt.</summary>
    private static string Answer(AiRequest request)
    {
        var prompt = request.SystemPrompt;

        return Mentions(prompt, "transcript editor") ? CleanedTranscript
            : Mentions(prompt, "chief of staff") ? MeetingSummary
            : Mentions(prompt, "project manager") ? ActionItems
            : Mentions(prompt, "secretary of record") ? DecisionLog
            : Mentions(prompt, "delivery lead") ? ImplementationPlan
            : Mentions(prompt, "procedure") ? StepList
            : Mentions(prompt, "risk manager") ? RiskRegister
            : Mentions(prompt, "stakeholders who fund") ? StakeholderUpdate
            : Mentions(prompt, "briefing an executive") ? ExecBrief
            : Mentions(prompt, "line editor") ? TightenedProse
            : Mentions(prompt, "data-shaping") ? Table
            : Mentions(prompt, "business analyst") ? Requirements
            : Mentions(prompt, "follow-up email") ? FollowUpEmail
            : Generic;
    }

    private static bool Mentions(string prompt, string phrase) =>
        prompt.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private const string CleanedTranscript = """
        **Priya:** Right, let's start with the Northwind migration. We're two weeks from the cutover
        and I want to know whether the reconciliation job is finished.

        **Tom:** It's finished for invoices. Credit notes are still failing on the currency rounding —
        about one in four hundred records.

        **Priya:** Is that a blocker for cutover, or something we fix afterwards?

        **Tom:** Fixable afterwards, but I'd want it in writing that finance accepts a manual
        reconciliation for the first month.
        """;

    private const string MeetingSummary = """
        **Outcome** — The Northwind migration stays on its 14 March cutover date, with credit-note
        reconciliation deferred to a post-cutover fix and a manual process covering the first month.

        ### Decisions
        - Cutover proceeds on 14 March. Owner: **Priya Raman**.
        - Credit-note rounding is fixed after cutover, not before. Owner: **Tom Selby**.
        - Finance accepts manual reconciliation for month one, subject to written sign-off.

        ### Action items
        | Action | Owner | Due |
        |---|---|---|
        | Get written sign-off on manual reconciliation | Priya Raman | 2026-03-07 |
        | Patch currency rounding on credit notes | Tom Selby | 2026-03-28 |
        | Draft the cutover comms for account managers | [owner?] | 2026-03-11 |

        ### Open questions
        - Who owns the customer-facing comms if cutover slips? Needs an answer from Priya.

        ### Risks
        - Manual reconciliation depends on two named people being available in the same week.
        """;

    private const string ActionItems = """
        - [ ] **Priya Raman** — get written sign-off from finance on manual reconciliation — _due: 2026-03-07_
        - [ ] **Tom Selby** — patch the currency rounding failure on credit notes — _due: 2026-03-28_
        - [ ] **[owner?]** — draft cutover comms for the account managers — _due: 2026-03-11_
        - [ ] **[owner?]** — confirm the rollback window with infrastructure — _due: [due?]_
        """;

    private const string DecisionLog = """
        | Decision | Rationale | Owner | Date | Follow-up |
        |---|---|---|---|---|
        | Cut over to Northwind on 14 March | Reconciliation is complete for invoices, which is 99.7% of volume | Priya Raman | 2026-02-24 | Confirm the rollback window |
        | Fix credit-note rounding after cutover | Affects ~1 in 400 records; not worth holding the date | Tom Selby | 2026-02-24 | Patch by 28 March |
        | Accept manual reconciliation for month one | Finance can absorb the volume at current rates | [owner?] | 2026-02-24 | Get the acceptance in writing |

        ### Considered but not decided
        - Delaying cutover by two weeks — stalled because the licence on the legacy system expires 31 March.
        """;

    private const string ImplementationPlan = """
        **Goal** — Northwind is the system of record for invoicing by 14 March, with no manual data
        entry required after the first month.

        **Assumptions**
        - The legacy licence genuinely expires on 31 March; there is no extension.
        - Finance has capacity for one month of manual credit-note reconciliation.

        ## Phase 1 — Freeze and verify
        **Objective:** the data going into cutover is known-good.
        **Steps:**
        1. Freeze schema changes on the legacy database.
        2. Run the reconciliation job against a full production copy.
        3. Record the credit-note failure rate and the affected currencies.
        **Owner:** Tom Selby
        **Depends on:** none
        **Done when:** the reconciliation report shows invoice parity and a quantified credit-note gap.

        ## Phase 2 — Cutover
        **Objective:** Northwind serves live invoicing.
        **Steps:**
        1. Take the legacy system read-only at 18:00 on 13 March.
        2. Run the final delta migration.
        3. Switch the invoicing endpoint to Northwind.
        4. Smoke-test three real invoices end to end.
        **Owner:** Priya Raman
        **Depends on:** Phase 1
        **Done when:** three invoices are issued from Northwind and appear in finance's ledger.

        ## Phase 3 — Close the gap
        **Objective:** no manual reconciliation from month two.
        **Steps:**
        1. Patch the currency rounding on credit notes.
        2. Re-run reconciliation over the month-one data.
        3. Hand the process back to finance.
        **Owner:** Tom Selby
        **Depends on:** Phase 2
        **Done when:** a full month reconciles with zero manual corrections.

        ## Open questions
        - Who approves the rollback if smoke tests fail? [TODO: confirm with Priya]
        """;

    private const string StepList = """
        **Before you start**
        - VPN access to the production network.
        - Write access to the `northwind-ops` repository.

        1. Open a terminal on the migration host.
        2. Run `ops migrate --dry-run --source legacy --target northwind`.
           - Expect: a summary ending `0 fatal, N warnings`.
        3. Review each warning in `./out/migrate-warnings.log`.
        4. Set the legacy system read-only.
           > **Warning:** this stops invoicing for everyone. Do it inside the agreed window only.
        5. Run `ops migrate --source legacy --target northwind`.
           - Expect: `migration complete` and a non-zero row count for `invoices`.
        6. Switch the endpoint with `ops route set invoicing northwind`.
           > **Note:** DNS propagation takes up to five minutes.
        7. Issue one test invoice and confirm it appears in the finance ledger.
        """;

    private const string RiskRegister = """
        | # | Risk | Likelihood | Impact | Early signal | Mitigation | Owner |
        |---|---|---|---|---|---|---|
        | 1 | Legacy licence expiry forces cutover before reconciliation is clean, leading to disputed invoices | Medium | High | Credit-note failure rate above 0.5% on 10 March | Book a contingency window on 21 March and pre-agree the manual process | Priya Raman |
        | 2 | Only two people understand the reconciliation job, so illness delays cutover (inferred) | Medium | High | Either Tom or Priya unavailable in cutover week | Pair a third engineer on Phase 1 | [owner?] |
        | 3 | Rounding fix changes historical totals, leading to a restated ledger | Low | High | Patch alters any pre-cutover row | Apply the fix forward-only and test against a ledger copy | Tom Selby |
        """;

    private const string StakeholderUpdate = """
        **Status:** 🟡 At risk — the date holds, but only with a manual process in month one.

        ### Since last update
        - Invoice reconciliation complete and verified against a full production copy.
        - Cutover window agreed with finance for 13–14 March.

        ### Next
        - Final delta migration and endpoint switch — Priya Raman, 14 March.
        - Currency rounding patch for credit notes — Tom Selby, 28 March.

        ### Needs a decision from you
        - Written acceptance of manual credit-note reconciliation for month one, by 7 March.

        ### Watching
        - The legacy licence expires 31 March, which removes any second attempt. Contingency window
          booked for 21 March.
        """;

    private const string ExecBrief = """
        **The Northwind migration will hit its 14 March date, but finance carries one month of manual
        work to get there.**

        - Invoice migration is verified; credit notes fail on currency rounding in roughly 1 in 400 records.
        - Holding the date avoids the 31 March legacy licence expiry, which would leave us with no system.
        - Cost of the workaround: about two finance-days per week for four weeks.
        - The engineering fix lands 28 March, after which the manual step disappears.

        **Ask:** Approve manual credit-note reconciliation for March, in writing, by 7 March.
        """;

    private const string TightenedProse = """
        The Northwind migration cuts over on 14 March. Invoice reconciliation is verified; credit
        notes still fail on currency rounding in about 1 in 400 records. We are fixing that after
        cutover, so finance reconciles credit notes manually for one month. Holding the date matters:
        the legacy licence expires 31 March, leaving no second attempt.
        """;

    private const string Table = """
        | Workstream | Owner | Status | Due |
        |---|---|---|---|
        | Invoice reconciliation | Tom Selby | Complete | 2026-02-21 |
        | Credit-note rounding | Tom Selby | In progress | 2026-03-28 |
        | Cutover comms | [owner?] | Not started | 2026-03-11 |
        | Finance sign-off | Priya Raman | Blocked | 2026-03-07 |
        """;

    private const string Requirements = """
        ### Functional
        - **REQ-01** — The system shall migrate all open invoices from the legacy database. _(source: "migrate all open invoices")_
        - **REQ-02** — The system shall reconcile credit notes against the legacy ledger. _(source: "reconcile the credit notes")_

        ### Non-functional
        - **REQ-03** — Cutover shall complete within a four-hour window. _(source: "four hours, no more")_
        - **REQ-04** — The system should keep the invoice endpoint available during the delta migration. _(source: "ideally no downtime")_

        ### Constraints
        - **REQ-05** — Cutover shall complete before 31 March. _(source: "licence expires end of March")_

        ### Needs clarification
        - Does the four-hour window include the rollback, or only the forward migration?
        """;

    private const string FollowUpEmail = """
        **Subject:** Northwind cutover confirmed for 14 March

        Hi all,

        Thanks for making the time this morning — particularly Tom for having the reconciliation
        numbers ready.

        - Cutover goes ahead on 14 March, in the 18:00–22:00 window.
        - Credit-note rounding is fixed after cutover, by 28 March.
        - Finance reconciles credit notes manually for the first month.

        **Next steps**
        - **Priya Raman** — obtain written sign-off from finance — _by 7 March_
        - **Tom Selby** — patch currency rounding on credit notes — _by 28 March_
        - **[owner?]** — draft cutover comms for account managers — _by 11 March_

        Priya, I need the finance sign-off in writing before 7 March so we can keep the date.

        Best,
        [Your name]
        """;

    private const string Generic = """
        Here is a worked answer against your note. In the real app this comes from the model you
        picked in Settings — a local Ollama model by default, or Claude if you supplied a key.

        - The assistant only ever sees the note text you point it at.
        - Rewrites replace the selection; summaries and plans are inserted below it.
        - Every change is a revision, so anything you dislike is one revert away.
        """;
}

/// <summary>Hands out the same canned assistant regardless of settings.</summary>
internal sealed class FakeAiAssistantFactory : IAiAssistantFactory
{
    private readonly FakeAiAssistant _assistant = new();

    public IAiAssistant Create(AiSettings settings) => _assistant;
}
