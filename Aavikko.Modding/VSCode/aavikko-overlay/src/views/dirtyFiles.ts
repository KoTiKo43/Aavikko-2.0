import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { DirtyEntry, StateManager } from '../state';

/**
 * Dirty Files — upstream files changed since HEAD, not yet captured.
 *
 * v0.2: source of truth is `git status --porcelain` (via Status.py) instead of
 * v0.1's mtime scan — correct after checkouts/touches, shows untracked files,
 * works for RobustToolbox too, and doesn't require overlay to be applied.
 */

export class DirtyFileItem extends vscode.TreeItem {
    constructor(
        readonly buildRoot: string,
        readonly entry: DirtyEntry,
    ) {
        super(entry.path, vscode.TreeItemCollapsibleState.None);
        this.filePath = path.join(buildRoot, entry.path);
        this.resourceUri = vscode.Uri.file(this.filePath);
        this.description = entry.status === '??' ? 'new' : 'modified';

        let stateIcon: string;
        let stateLabel: string;
        if (entry.has_overlay) {
            stateIcon = entry.type === 'resources' ? 'package' : 'edit';
            stateLabel = entry.type === 'resources' ? 'Copy exists in overlay' : 'Patch exists';
        } else {
            stateIcon = 'sparkle';
            stateLabel = 'New (no overlay file yet)';
        }
        this.iconPath = new vscode.ThemeIcon(stateIcon);

        const typeLabel = entry.type === 'resources'
            ? 'Resources (direct copy)'
            : entry.type === 'robust'
                ? 'RobustToolbox (.cs.patch)'
                : 'Content (.cs.patch)';
        let mtime = '';
        try {
            mtime = `\nModified: ${formatTimeAgo(fs.statSync(this.filePath).mtimeMs)}`;
        } catch { /* file may be gone */ }
        this.tooltip = `${entry.path}\nState: ${stateLabel}\nType: ${typeLabel}${mtime}`;

        this.contextValue = entry.has_overlay ? 'dirtyFileWithPatch' : 'dirtyFileNew';
        this.command = {
            command: 'aavikko.openDirtyFile',
            title: 'Open File',
            arguments: [this.filePath],
        };
    }

    readonly filePath: string;
}

class GroupItem extends vscode.TreeItem {
    constructor(label: string, readonly children: DirtyFileItem[], icon: string) {
        super(label, children.length
            ? vscode.TreeItemCollapsibleState.Expanded
            : vscode.TreeItemCollapsibleState.Collapsed);
        this.iconPath = new vscode.ThemeIcon(icon);
        this.description = `${children.length}`;
    }
}

export class DirtyFilesProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly _onDidChange = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this._onDidChange.event;

    constructor(
        private readonly buildRoot: string,
        private readonly state: StateManager,
    ) {
        state.onDidChange(() => this._onDidChange.fire());
    }

    refresh(): void {
        this._onDidChange.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
        if (element instanceof GroupItem) {
            return element.children;
        }
        if (element) {
            return [];
        }
        const dirty = this.state.current.dirty;
        if (!dirty.length) {
            return [];
        }
        const groups: Array<[string, string, DirtyEntry[]]> = [
            ['Content', 'file-code', []],
            ['Resources', 'file-media', []],
            ['RobustToolbox', 'gear', []],
        ];
        for (const entry of dirty) {
            const idx = entry.type === 'resources' ? 1 : entry.type === 'robust' ? 2 : 0;
            groups[idx][2].push(entry);
        }
        const items: vscode.TreeItem[] = [];
        for (const [label, icon, entries] of groups) {
            if (!entries.length) {
                continue;
            }
            // Most recently modified first
            const children = entries
                .map(e => new DirtyFileItem(this.buildRoot, e))
                .sort((a, b) => mtimeSafe(b.filePath) - mtimeSafe(a.filePath));
            items.push(new GroupItem(label, children, icon));
        }
        return items;
    }
}

function mtimeSafe(p: string): number {
    try {
        return fs.statSync(p).mtimeMs;
    } catch {
        return 0;
    }
}

export function formatTimeAgo(mtimeMs: number): string {
    const diff = Date.now() - mtimeMs;
    if (diff < 60_000) { return 'just now'; }
    if (diff < 3_600_000) { return `${Math.floor(diff / 60_000)}m ago`; }
    if (diff < 86_400_000) { return `${Math.floor(diff / 3_600_000)}h ago`; }
    return `${Math.floor(diff / 86_400_000)}d ago`;
}
