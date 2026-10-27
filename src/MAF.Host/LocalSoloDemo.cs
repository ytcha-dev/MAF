using MAF.Core;
using MAF.Core.Policy;
using MAF.Workflows.External;

namespace MAF.Host;

// One in-memory product journey. Fixed scripted participants cannot construct a live client,
// authority, journal, process or transport. Reuse the existing typed Workflow and presenter.
internal static class LocalSoloDemo
{
    internal static async Task<int> RunAsync(LocalTaskPreview preview, string scenario, TextReader input, TextWriter output, TextWriter error)
    {
        LocalSoloPresentation.Preview(output, preview);
        output.WriteLine("Offline Demo / ScriptedParticipants / NotLiveProvider");
        output.WriteLine("Actual participants: PCA=scripted; Harness=OfflineDemo (not OpenCode); Reviewer=scripted. Models: none.");
        output.WriteLine("Data flow: in-memory PCA -> fixed offline Harness -> Reviewer -> HumanGate; no workspace content read or sent.");
        output.WriteLine("Configured Harness/models above are candidate metadata only; this demo does not use them.");
        output.WriteLine($"Scenario: {scenario}; no filesystem writes, persistence, network or model execution.");
        output.WriteLine("Type run-demo to confirm this offline task, or anything else to cancel. This is not Live consent or authorization.");
        if (input.ReadLine() != "run-demo")
        {
            output.WriteLine("Cancelled / NotExecuted; no demo task created.");
            return 0;
        }

        var task = new HandoffTask("demo-" + Guid.NewGuid().ToString("N"), "demo-run-" + Guid.NewGuid().ToString("N"),
            preview.Profile.Workspace!, preview.Objective, TimeSpan.FromSeconds(LocalTaskPreview.BudgetSeconds));
        output.WriteLine($"Task created once: {task.TaskId}");
        LocalResultView view;
        var sessionExit = 0;
        try
        {
            var outcome = await ExecuteAsync(task, scenario, output);
            view = LocalSoloOutcomeMapper.FromControlledWorkflow(outcome);
        }
        catch (Exception)
        {
            // A created task must not disappear behind the inspection command's pre-start error.
            // Preserve its identity, omit unknown execution identity and never retry the journey.
            sessionExit = 1;
            view = new()
            {
                Kind = LocalViewKind.ControlledOfflineFixture, TaskId = task.TaskId, WorkflowRunId = task.WorkflowRunId,
                Harness = "OfflineDemo", Status = ExternalExecutionStatus.Unknown,
                Summary = "Offline workflow did not return a usable outcome; no live provider was invoked.",
                Limitations = ["Demo outcome unavailable; no automatic retry. Execution identity and evidence are unavailable."],
                Readiness = HumanGateStatus.NeedsHumanReview
            };
        }
        LocalSoloPresentation.Result(output, view);
        output.WriteLine("Commands: status | result | submit-live (blocked) | quit. Task is session-only; restart cannot recover it.");
        while (input.ReadLine() is { } command && command != "quit")
        {
            switch (command)
            {
                case "status":
                    output.WriteLine($"Task: {task.TaskId}; Status: {view.Status}; Current worker: none; HumanGate: {view.Readiness}");
                    output.WriteLine("Offline snapshot; status/result never submit, retry or resume work.");
                    if (view.Status == ExternalExecutionStatus.Unknown)
                        output.WriteLine("Unknown is not Failed or Completed; no automatic resubmission.");
                    if (view.Status == ExternalExecutionStatus.WaitingForApproval)
                        output.WriteLine("Safely stopped at WaitingForApproval; no permission reply or automatic approval.");
                    break;
                case "result": LocalSoloPresentation.Result(output, view); break;
                case "submit-live":
                    try { LocalSoloApplication.Submit(); }
                    catch (ExternalHarnessException) { error.WriteLine("Live submission: Blocked / " + LocalSoloApplication.Blocker); }
                    break;
                default: output.WriteLine("Unknown command; use status | result | submit-live | quit. No action attempted."); break;
            }
        }
        output.WriteLine("Offline session closed; no durable task or live execution created.");
        return sessionExit;
    }

    internal static async Task<WorkflowOutcome> ExecuteAsync(HandoffTask task, string scenario, TextWriter output)
    {
        var status = scenario switch
        {
            "completed" => ExternalExecutionStatus.Completed,
            "failed" => ExternalExecutionStatus.Failed,
            "unknown" => ExternalExecutionStatus.Unknown,
            "waiting" => ExternalExecutionStatus.WaitingForApproval,
            _ => throw new ArgumentException("Invalid offline scenario")
        };
        var handle = new ExternalExecutionHandle("demo-execution-" + Guid.NewGuid().ToString("N"), "OfflineDemo");
        var participants = new Participants(task, handle, status, output);
        var workflow = new ControlledHandoffWorkflow(participants,
            request => request == new ExternalTaskRequest(task.TaskId, task.Workspace, task.Objective, task.StageTimeout)
                ? PolicyOutcome.Allow : PolicyOutcome.Deny, // Exact offline assessment only; not provider admission.
            participants, participants.ReviewAsync, handle, TimeProvider.System);
        var run = await workflow.RunAsync(task);
        output.WriteLine("Current stage: HumanGate / no automatic acceptance or permission reply.");
        return run.Outcome;
    }

    sealed class Participants(HandoffTask original, ExternalExecutionHandle handle, ExternalExecutionStatus status,
        TextWriter output) : ITaskBriefing, IControlledDelegation
    {
        public Task<TaskBrief> BriefAsync(HandoffTask task, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (task != original) throw new ArgumentException("DemoIdentityMismatch");
            output.WriteLine("Current stage: PCA / scripted briefing / no model.");
            return Task.FromResult(new TaskBrief(task.TaskId, task.Workspace, task.Objective,
                ExternalExecutionPolicy.ReadOnly, ComprehensionStatus.Proposed, [], ["Scripted briefing; no source inspection."]));
        }

        public Task<ExternalTaskResult> DelegateAsync(ExternalTaskRequest request, string run, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request != new ExternalTaskRequest(original.TaskId, original.Workspace, original.Objective, original.StageTimeout)
                || run != original.WorkflowRunId) throw new ArgumentException("DemoIdentityMismatch");
            output.WriteLine("Current stage: Harness / OfflineDemo scripted result / OpenCode not started.");
            return Task.FromResult(new ExternalTaskResult(original.TaskId, run, status, handle,
                "Scripted scenario outcome only; no answer about workspace content was generated.",
                ["No provider, model or source verification; this is not live task success."], []));
        }

        internal Task<ReviewAssessment> ReviewAsync(TaskEvidence evidence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (evidence.Task != original || evidence.ExternalResult.Handle != handle)
                throw new ArgumentException("DemoIdentityMismatch");
            output.WriteLine("Current stage: Reviewer / original typed task and Harness result received automatically / no copy-paste.");
            return Task.FromResult(new ReviewAssessment(evidence.Task.TaskId, evidence.ExternalResult.Handle.ExecutionId,
                EvidenceAssessmentStatus.InsufficientEvidence, "Scripted review: no independently verified source evidence.",
                [], ["Automatic handoff demonstrated; actual model review and semantic correctness remain unverified."]));
        }
    }
}
