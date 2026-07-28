# Paired end-to-end latency benchmark

This benchmark compares OpenVocare's no-rewrite path with the official ChatGPT
desktop `Ctrl+M` dictation shortcut on one Windows 11 x64 system. It is a
reproducible point measurement, not a universal performance claim.

## Test conditions

Both implementations received the same approximately five-second English
recording through VB-Audio Virtual Cable. The expected transcript was:

> Hello, how are you doing today? I'm fine, great, thanks.

Two independent sessions were run on 2026-07-27. Each session used one
discarded warm-up per implementation followed by ten measured pairs.
Odd-numbered pairs ran ChatGPT desktop first; even-numbered pairs ran
OpenVocare first. This alternating AB/BA order reduces ordering and transient
network bias.

Timing started when the dictation shortcut was released and stopped when the
completed matching text appeared in the benchmark editor. A sample counted only
when it matched the expected recording and appeared successfully. The harness
temporarily routed the Windows capture default through VB-CABLE and restored it
after every trial. OpenVocare's original microphone selection was also restored
after each session.

## Discarded warm-ups

| Session | ChatGPT desktop `Ctrl+M` | OpenVocare |
|---:|---:|---:|
| 1 | 3,903.5 ms | 3,921.6 ms |
| 2 | 4,492.5 ms | 3,346.8 ms |

## Raw measured samples

### Session 1

| Pair | ChatGPT desktop `Ctrl+M` (ms) | OpenVocare (ms) | Faster path |
|---:|---:|---:|---|
| 1 | 3,818.5 | 3,390.9 | OpenVocare |
| 2 | 4,528.6 | 2,975.8 | OpenVocare |
| 3 | 1,220.9 | 3,245.6 | Official |
| 4 | 3,804.2 | 3,600.5 | OpenVocare |
| 5 | 3,637.8 | 3,586.0 | OpenVocare |
| 6 | 4,332.2 | 3,237.3 | OpenVocare |
| 7 | 4,853.1 | 3,941.3 | OpenVocare |
| 8 | 3,871.4 | 3,896.3 | Official |
| 9 | 4,237.7 | 3,182.4 | OpenVocare |
| 10 | 3,794.3 | 3,588.4 | OpenVocare |

### Session 2 replication

| Pair | ChatGPT desktop `Ctrl+M` (ms) | OpenVocare (ms) | Faster path |
|---:|---:|---:|---|
| 1 | 6,464.8 | 3,032.8 | OpenVocare |
| 2 | 4,249.5 | 7,416.8 | Official |
| 3 | 4,528.1 | 3,133.8 | OpenVocare |
| 4 | 4,271.0 | 3,437.7 | OpenVocare |
| 5 | 4,922.4 | 3,776.7 | OpenVocare |
| 6 | 4,859.7 | 3,385.8 | OpenVocare |
| 7 | 5,070.7 | 3,252.1 | OpenVocare |
| 8 | 4,021.1 | 3,808.5 | OpenVocare |
| 9 | 4,256.2 | 3,415.0 | OpenVocare |
| 10 | 5,264.2 | 3,273.6 | OpenVocare |

All 40 measured samples matched the spoken words, pasted successfully, and
completed on their first attempt. Two OpenVocare samples used a period instead
of a comma between "fine" and "great"; this punctuation-only difference passed
the harness's expected-audio validation.

## Results

### Per-session results

| Session | Official mean | OpenVocare mean | Mean advantage | Pair wins |
|---:|---:|---:|---:|---:|
| 1 | 3,809.9 ms | 3,464.4 ms | 345.4 ms (9.1%) | 8/10 |
| 2 | 4,790.8 ms | 3,793.3 ms | 997.5 ms (20.8%) | 9/10 |

The first session's paired 95% confidence interval was -359.2 to 1,050.0 ms.
The replication's interval was -218.5 to 2,213.5 ms. Neither ten-pair session
alone established a conclusive difference.

### Combined 20-pair result

| Metric | ChatGPT desktop `Ctrl+M` | OpenVocare |
|---|---:|---:|
| Correct pastes | 20/20 | 20/20 |
| Mean | 4,300.3 ms | 3,628.9 ms |
| Median | 4,263.6 ms | 3,402.9 ms |
| P95 | 5,324.2 ms | 4,115.0 ms |
| Minimum | 1,220.9 ms | 2,975.8 ms |
| Maximum | 6,464.8 ms | 7,416.8 ms |
| Population standard deviation | 955.1 ms | 910.2 ms |

Across both sessions, OpenVocare was 671.5 ms faster on average, a 15.6%
reduction, and won 17 of 20 pairs. The median paired advantage was 876.5 ms.
The paired mean difference had a 95% confidence interval of approximately
19.6 to 1,323.4 ms, where positive values favor OpenVocare.

Both products produced legitimate high-variance samples. In particular, the
official path completed one sample in 1,220.9 ms while OpenVocare took
7,416.8 ms on another. Neither value was removed: both appeared after shortcut
release, matched the recording, and pasted successfully.

The defensible claim is therefore limited to this controlled test: OpenVocare
averaged about 0.67 seconds faster across 20 paired trials on this machine,
network, recording, and date. The result does not guarantee the same advantage
for other users, recordings, service conditions, or future ChatGPT/Codex
versions.
