import { t } from '../i18n';
import { showToast } from '../toast';

export type RuleChoiceKind = 'chow' | 'concealedKong' | 'addedKong';

export interface RuleTileChoice {
  tileIds: ReadonlyArray<number>;
  label: () => string;
}

export class RuleActionChoice {
  private readonly dialog: HTMLDialogElement;
  private readonly title: HTMLElement;
  private readonly hint: HTMLElement;
  private readonly options: HTMLElement;
  private readonly cancel: HTMLButtonElement;
  private context: string | null = null;
  private labels: Array<{ label: HTMLElement; detail: HTMLElement; choice: RuleTileChoice; ids: number[] }> = [];

  constructor() {
    this.dialog = document.getElementById('rule-action-choice-dialog') as HTMLDialogElement;
    this.title = document.getElementById('rule-action-choice-title')!;
    this.hint = document.getElementById('rule-action-choice-hint')!;
    this.options = document.getElementById('rule-action-choices')!;
    this.cancel = document.getElementById('rule-action-choice-cancel') as HTMLButtonElement;
    this.cancel.onclick = () => this.close();
    this.dialog.addEventListener('cancel', (event) => {
      event.preventDefault();
      this.close();
    });
    // Native dialog Escape cancels a choice, not the underlying discard claim.
    this.dialog.addEventListener('keydown', (event) => event.stopPropagation());
    this.dialog.addEventListener('keyup', (event) => event.stopPropagation());
    this.localize();
  }

  get contextKey(): string | null {
    return this.context;
  }

  localize(): void {
    this.title.textContent = t(this.dialog.dataset.kind === 'chow'
      ? 'actions.choose_chow' : 'actions.choose_kong');
    this.hint.textContent = t('actions.choice_hint');
    this.cancel.textContent = t('common.cancel');
    for (const { label, detail, choice, ids } of this.labels) {
      label.textContent = choice.label();
      detail.textContent = t('actions.physical_tiles', { ids: ids.join(', ') });
    }
  }

  show(
    kind: RuleChoiceKind,
    context: string,
    choices: ReadonlyArray<RuleTileChoice>,
    choose: (tileIds: number[], context: string) => void,
  ): void {
    if (this.context === context && this.dialog.dataset.kind === kind && this.dialog.open) return;
    this.close();
    this.context = context;
    this.dialog.dataset.kind = kind;
    this.localize();
    this.options.replaceChildren();
    for (const choice of choices) {
      const tileIds = [...choice.tileIds];
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'rule-action-choice';
      button.dataset.tileIds = tileIds.join(',');
      const label = document.createElement('span');
      label.textContent = choice.label();
      const detail = document.createElement('small');
      detail.className = 'rule-action-choice-detail';
      detail.textContent = t('actions.physical_tiles', { ids: tileIds.join(', ') });
      button.append(label, detail);
      this.labels.push({ label, detail, choice, ids: tileIds });
      button.onclick = () => {
        if (this.context !== context || !this.dialog.open) {
          showToast(t('actions.unavailable'), 'info');
          return;
        }
        this.close();
        choose(tileIds, context);
      };
      this.options.appendChild(button);
    }
    this.dialog.showModal();
  }

  close(): void {
    this.context = null;
    if (this.dialog.open) this.dialog.close();
    this.options.replaceChildren();
    this.labels = [];
  }
}
