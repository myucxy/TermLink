using System;
using TermLink.SessionHost;

var single = new ResizeSettlePolicy(80, 24, 140, 250);
single.Observe(100, 30, 1_000);
Require(single.GetDelayUntilDue(1_000) == 140,
    "a single resize did not schedule its exact trailing settle deadline");
Require(!single.TryGetDue(1_139, out _, out _),
    "a single resize was applied before the trailing settle window");
Require(single.TryGetDue(1_140, out var columns, out var rows) && columns == 100 && rows == 30,
    "a single resize was not due at the trailing settle boundary");
single.MarkApplied(columns, rows);
Require(!single.TryGetDue(2_000, out _, out _),
    "an applied size remained pending");
single.Observe(100, 30, 2_001);
Require(!single.TryGetDue(3_000, out _, out _),
    "an unchanged applied size was scheduled again");

var burst = new ResizeSettlePolicy(80, 24, 140, 250);
burst.Observe(90, 25, 0);
burst.Observe(100, 30, 80);
burst.Observe(110, 35, 160);
Require(burst.GetDelayUntilDue(240) == 10,
    "a continuous burst did not shorten polling to its maximum-wait deadline");
Require(!burst.TryGetDue(249, out _, out _),
    "a resize burst exceeded neither settle nor maximum wait but became due");
Require(burst.TryGetDue(250, out columns, out rows) && columns == 110 && rows == 35,
    "a continuous resize burst did not yield its latest value at maximum wait");
burst.MarkApplied(columns, rows);

burst.Observe(120, 40, 251);
burst.Observe(130, 45, 320);
Require(!burst.TryGetDue(459, out _, out _),
    "a second burst settled before its final trailing window");
Require(burst.TryGetDue(460, out columns, out rows) && columns == 130 && rows == 45,
    "a second burst did not settle to its final size");

var retry = new ResizeSettlePolicy(80, 24, 140, 250);
retry.Observe(90, 30, 0);
Require(retry.TryGetDue(140, out columns, out rows),
    "a due resize was unavailable for its first ConPTY attempt");
Require(retry.TryGetDue(180, out var retryColumns, out var retryRows) &&
    retryColumns == columns && retryRows == rows,
    "a failed ConPTY attempt discarded the pending resize");
retry.MarkApplied(retryColumns, retryRows);

var reverted = new ResizeSettlePolicy(80, 24, 140, 250);
reverted.Observe(100, 30, 0);
reverted.Observe(80, 24, 50);
Require(!reverted.TryGetDue(1_000, out _, out _),
    "a size that returned to the applied dimensions was not cancelled");

Console.WriteLine("SessionHost resize settle validation passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
