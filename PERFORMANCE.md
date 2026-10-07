# Performance

Status: **preliminary**. The measurements below were taken on 2026-10-07. The scripted measurement of search, filter and scrolling with large generated test data (`--perf-report`) is implemented but has not been run yet; those values are open.

## Test environment

- Virtual machine: Windows Server 2025 Standard (build 26100), 4 logical processors (Intel Xeon, Skylake), 24 GB RAM.
- Native app 0.9.0: Release, self-contained, win-x64, ReadyToRun (`build.ps1` publish output).
- Electron app 0.4.0: `dist-release\win-unpacked` build of the last Electron commit (`8160f03`).
- Data: 3 systems (native test data folder) and 1 system (Electron data). No sessions open.

## Method

1. **Time to visible window and memory**: [tools/perf/Measure-Startup.ps1](tools/perf/Measure-Startup.ps1). The same method is used for both apps: start the process, poll every 10 ms until a process with that image name has a visible, titled top-level window, wait 10 s, sum the working set and private bytes of all processes with that name. 5 runs, median reported. The first run after a build is a cold start.
2. **Startup phases (native)**: the app logs milliseconds since process start (`Process.StartTime`) for `main`, `resources-loaded`, `window-created`, `window-rendered`, `data-loaded` and `list-usable` in `app.log`. "list-usable" is the first idle point of the dispatcher after the system list was rendered.
3. **Scripted UI timings (native)**: `--perf-report <file>` (unpackaged builds) runs search, filter, view switch, page navigation and a full scroll of the systems list. Each timing includes layout and rendering (the clock stops when the dispatcher reaches ContextIdle), median of 5 runs. Use it only with test data in a separate `--data-dir` (system names `PERFTEST-*`, hosts `*.example.invalid`).

## Results

| Measurement | Electron 0.4.0 | Native 0.9.0 |
|---|---|---|
| Process start → visible main window (median of 5, warm) | **638 ms** | 1143 ms |
| Visible window, cold start (first run) | 3734 ms | n/a (not measured with this script) |
| Processes after start | 4 | **1** |
| Working set after start (sum) | 316 MB | **130 MB** |
| Private memory after start (sum) | 104 MB | **46 MB** |

Native startup phases (warm, median of 3, ms since process start):

| Phase | Before deferral | After deferral |
|---|---|---|
| `main` (runtime started) | 97 | 112–124 |
| `resources-loaded` | 357 | 397–408 |
| `window-created` | 709 | 760–789 |
| `window-rendered` | 1120 | 1213–1273 |
| `list-usable` | 1725–1789 | 1565–1633 |

Installed MSIX (packaged, real data, 1 system): window rendered at 1218–1407 ms, list usable at 1914–2175 ms (warm). The first start right after installation took 7.6 s to render, which points to a first-run scan of the newly installed files.

Effect of runtime settings on time to visible window (median of 3): default 1185 ms, ReadyToRun off 3933 ms, tiered PGO off 1130 ms, tiered compilation off 1208 ms. ReadyToRun is therefore essential, and the remaining time is not JIT.

## Optimizations done

- ReadyToRun for the app and the self-contained framework.
- Window first, data on a background thread, services composed afterwards.
- Tray (WinForms), jump list, reachability checks, central list, Entra ID and update check start only when the dispatcher is idle after the first list frame. This cut the gap between "rendered" and "usable" from about 600 ms to about 330 ms.
- Virtualized and recycled list rows, debounced search on prebuilt keys, no list rebuild on status updates, frozen icon geometries drawn directly in `OnRender`, no effects or animations except the busy spinner.
- Source-generated JSON (no reflection), background file writes.

## Bottlenecks and next steps

- Time to a visible window is about 0.5 s slower than Electron. About 280 ms go to loading the application resources, about 330 ms to creating the main window and about 450 ms to the first layout and render. Next steps:
  - Profile these phases with a CPU sampling trace (ETW).
  - Test whether the font fallback for the missing Aptos font costs time.
  - Split the resource dictionaries so that dialog and list templates load on first use.
  - Simplify the first frame (sidebar, top bar, skeleton only).
  - Consider a native splash image.
- Run `--perf-report` with 5,000 generated systems and add search, filter, scroll and view timings to this table.
- Measure memory after typical use (several sessions, all pages visited).
