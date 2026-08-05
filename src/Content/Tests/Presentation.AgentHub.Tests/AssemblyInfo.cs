using Xunit;

// Issue #261. This assembly's telemetry tests observe metric emission through OpenTelemetry's
// process-global meter and listener plumbing. Run in parallel, one test's turn emits into the
// window another test is measuring, so roughly one test failed per run — a DIFFERENT one each
// time, and every one of them passing in isolation. It failed on unmodified main too, so it is
// not a regression; it is a suite that had been quietly unreliable and finally started failing
// pull requests.
//
// Serialising the assembly is deliberately blunt, and it is the same call already made and
// documented in Presentation.ExecutionApi.Tests. A shared collection covering only the telemetry
// classes would be narrower, but it depends on every future test author remembering to join it,
// and the failure mode when one forgets is an intermittent test that reads as a product defect —
// which is exactly the state this is fixing.
//
// The cost is real and was measured, not guessed: roughly 20 seconds parallel against 50-56
// seconds serial, so this more than doubles the assembly's wall clock. It buys five consecutive
// 410/410 runs where the same command previously failed two or three tests every time. Half a
// minute is a fair price for a gate whose green means something; if that ever stops being true,
// the narrower fix is a shared collection over the Telemetry namespace alone.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
