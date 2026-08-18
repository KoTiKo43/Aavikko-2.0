import * as vscode from 'vscode';
import { StateManager } from './state';

/**
 * Status bar — persistent state indicator.
 *
 * v0.2: three visual states (applied / pristine / applied-with-issues) and
 * click opens a QuickPick action menu instead of blindly toggling.
 */
export class AavikkoStatusBar {
    private readonly item: vscode.StatusBarItem;

    constructor(private readonly state: StateManager) {
        this.item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
        this.item.command = 'aavikko.showActions';
        state.onDidChange(() => this.update());
        this.update();
        this.item.show();
    }

    private update(): void {
        const st = this.state.current;
        this.item.backgroundColor = undefined;
        if (st.state === 'applied') {
            const patches =
                (st.applied?.cs_patches_applied?.length ?? 0) +
                (st.applied?.robust_patches_applied?.length ?? 0);
            const failed = st.applied?.cs_patches_failed?.length ?? 0;
            const dirty = st.dirty.length;
            const suffix = dirty > 0 ? ` · ${dirty} dirty` : '';
            this.item.text = `$(package) Aavikko: Applied (${patches}p${suffix})`;
            const lines = [
                `Overlay applied at ${st.applied?.at ?? '?'}`,
                `Patches: ${patches}` + (failed ? ` (${failed} FAILED)` : ''),
            ];
            if (dirty > 0) {
                lines.push(`${dirty} uncaptured change(s) — run Generate`);
            }
            lines.push('', 'Click for actions');
            this.item.tooltip = lines.join('\n');
            if (failed > 0) {
                this.item.backgroundColor =
                    new vscode.ThemeColor('statusBarItem.warningBackground');
            }
        } else {
            this.item.text = '$(circle-outline) Aavikko: Pristine';
            this.item.tooltip = 'Upstream is clean\nClick for actions';
        }
    }

    async showActions(): Promise<void> {
        const st = this.state.current;
        const applied = st.state === 'applied';
        interface Action extends vscode.QuickPickItem {
            run: () => unknown;
        }
        const cmd = (id: string) => async () => { await vscode.commands.executeCommand(id); };
        const actions: Action[] = [
            applied
                ? { label: '$(discard) Clear Overlay', description: 'revert upstream to pristine', run: cmd('aavikko.clear') }
                : { label: '$(check) Apply Overlay', description: 'apply patches + copy mods', run: cmd('aavikko.apply') },
            { label: '$(diff) Generate All Patches', description: 'capture modified files into overlay', run: cmd('aavikko.generateAll') },
            { label: '$(shield) Check Conflicts', description: 'detect upstream conflicts', run: cmd('aavikko.runCheck') },
            { label: '$(verified) Validate Overlay', description: 'sanity-check placement', run: cmd('aavikko.runValidate') },
            { label: '$(refresh) Refresh Views', description: 're-read status', run: cmd('aavikko.refreshAll') },
            { label: '$(output) Show Log', description: 'Aavikko output channel', run: cmd('aavikko.showStatus') },
        ];
        const picked = await vscode.window.showQuickPick(actions, {
            title: `Aavikko — ${applied ? 'overlay applied' : 'upstream pristine'}`,
            placeHolder: 'Choose an action',
        });
        if (picked) {
            await picked.run();
        }
    }

    dispose(): void {
        this.item.dispose();
    }
}
