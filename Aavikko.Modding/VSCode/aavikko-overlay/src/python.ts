import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as path from 'path';
import { log, logError } from './logger';

/**
 * Python bridge — runs Patcher scripts safely.
 *
 * Key improvements over v0.1:
 *  - execFile with argv arrays: no shell, no quoting/injection bugs
 *  - python3 → python → py auto-detection (Windows support)
 *  - cached interpreter lookup, invalidated on settings change
 */

export interface RunResult {
    stdout: string;
    stderr: string;
    code: number;
}

let cachedPython: string | null | undefined;

export function invalidatePythonCache(): void {
    cachedPython = undefined;
}

async function probe(cmd: string): Promise<boolean> {
    return new Promise((resolve) => {
        cp.execFile(cmd, ['--version'], { timeout: 5000 }, (err) => resolve(!err));
    });
}

/** Resolve the Python interpreter: setting → python3 → python → py. */
export async function getPython(): Promise<string | null> {
    if (cachedPython !== undefined) {
        return cachedPython;
    }
    const configured = vscode.workspace.getConfiguration('aavikko').get<string>('pythonPath', '');
    if (configured) {
        if (await probe(configured)) {
            return (cachedPython = configured);
        }
        logError(`Configured aavikko.pythonPath "${configured}" does not run; falling back to auto-detect`);
    }
    for (const candidate of ['python3', 'python', 'py']) {
        if (await probe(candidate)) {
            log(`Python interpreter: ${candidate}`);
            return (cachedPython = candidate);
        }
    }
    cachedPython = null;
    return null;
}

/** Run a Patcher script non-interactively. Returns null if Python missing. */
export async function runScript(
    patcherDir: string,
    script: string,
    args: string[] = [],
    timeoutMs = 120_000,
): Promise<RunResult | null> {
    const python = await getPython();
    if (!python) {
        vscode.window.showErrorMessage(
            'Aavikko: Python not found. Install Python 3 or set aavikko.pythonPath.');
        return null;
    }
    log(`$ ${python} ${script} ${args.join(' ')}  (cwd: ${patcherDir})`);
    return new Promise((resolve) => {
        cp.execFile(
            python,
            [path.join(patcherDir, script), ...args],
            { cwd: patcherDir, timeout: timeoutMs, maxBuffer: 32 * 1024 * 1024 },
            (err, stdout, stderr) => {
                const code = err && typeof err.code === 'number' ? err.code : 0;
                resolve({ stdout: stdout ?? '', stderr: stderr ?? '', code });
            },
        );
    });
}

/**
 * Run a Patcher script in the integrated terminal (for interactive scripts
 * like Check.py with prompts). Reuses a single "Aavikko" terminal.
 */
let terminal: vscode.Terminal | null = null;

export function runScriptInTerminal(patcherDir: string, commandLine: string): void {
    if (terminal && !vscode.window.terminals.includes(terminal)) {
        terminal = null; // closed by user
    }
    if (!terminal) {
        terminal = vscode.window.createTerminal('Aavikko');
    }
    terminal.show(true);
    terminal.sendText(`cd "${patcherDir}" && ${commandLine}`);
}

export function disposeTerminal(): void {
    terminal?.dispose();
    terminal = null;
}
