import { test, expect, type Locator } from '@playwright/test';
import { newActor, applyRoom, closeLobby } from './_lobby-repair';

async function expectReadableText(locator: Locator): Promise<void> {
  await expect.poll(() => locator.evaluate(element => {
    const node = element as HTMLElement;
    const box = node.getBoundingClientRect();
    const style = getComputedStyle(node);
    const range = document.createRange();
    range.selectNodeContents(node);
    const text = Array.from(range.getClientRects()).filter(rect => rect.width > 0 && rect.height > 0);
    const left = box.left + parseFloat(style.paddingLeft) + parseFloat(style.borderLeftWidth);
    const right = box.right - parseFloat(style.paddingRight) - parseFloat(style.borderRightWidth);
    return {
      hasText: (node.textContent?.trim().length ?? 0) > 0 && text.length > 0,
      fitsWidth: node.scrollWidth <= node.clientWidth + 1
        && text.every(rect => rect.left >= left - 1 && rect.right <= right + 1),
      inViewport: text.every(rect => rect.left >= 0 && rect.right <= innerWidth
        && rect.top >= 0 && rect.bottom <= innerHeight),
      // A DOM-visible row can still be clipped by the outer social/room scroller.
      painted: text.every(rect => [rect.left + 1, (rect.left + rect.right) / 2, rect.right - 1].every(x =>
        [rect.top + 1, (rect.top + rect.bottom) / 2, rect.bottom - 1].every(y =>
          node.contains(document.elementFromPoint(x, y))))),
    };
  })).toEqual({ hasText: true, fitsWidth: true, inViewport: true, painted: true });
}

async function expectSeparatePanels(chat: Locator, spectator: Locator): Promise<void> {
  await expect.poll(async () => {
    const [a, b] = await Promise.all([chat.boundingBox(), spectator.boundingBox()]);
    return a !== null && b !== null && (
      a.x + a.width <= b.x || b.x + b.width <= a.x
      || a.y + a.height <= b.y || b.y + b.height <= a.y
    );
  }, { message: 'Chat and spectator controls must not overlap' }).toBe(true);
}

for (const viewport of [
  { width: 1280, height: 900 }, { width: 390, height: 844 }, { width: 844, height: 390 },
]) {
  test(`chat initially labels its channel, opens readable options and visibly restores messages ${viewport.width}x${viewport.height}`,
    async ({ browser, baseURL }, info) => {
      test.setTimeout(60_000);
      const compact = viewport.width <= 900 || viewport.height <= 520;
      const context = await browser.newContext({
        viewport, hasTouch: compact, isMobile: compact, deviceScaleFactor: 1, locale: 'en-US',
      });
      try {
        const actor = await newActor(browser, baseURL, info, 'chat-readability', context);
        const page = actor.page;
        await applyRoom(actor, 0);
        await closeLobby(actor);
        const toggle = page.getByTestId('chat-toggle');
        await expect(toggle).toHaveAccessibleName('Open chat');
        await toggle.click();
        const native = page.getByTestId('chat-channel-select');
        const wrapper = native.locator('..');
        const trigger = wrapper.getByRole('button', { name: 'Chat channel', exact: true });
        const popup = wrapper.getByRole('listbox', { name: 'Chat channel', exact: true });

        // Assert the first render, before opening or choosing anything repairs it.
        await expect(native).toHaveValue('table');
        await expect(trigger).toHaveText('Table');
        await expectReadableText(trigger);
        expect((await trigger.boundingBox())!.width).toBeGreaterThanOrEqual(110);
        if (!compact) {
          await expect(page.getByTestId('chat-collapse')).toHaveAccessibleName('Collapse chat');
          await expect(page.getByTestId('chat-collapse')).toBeInViewport({ ratio: 1 });
        }
        await trigger.press('ArrowDown');
        await expect(popup).toBeInViewport({ ratio: 1 });
        await expect(popup.getByRole('option')).toHaveText(['Table', 'Private']);
        await expect.poll(() => popup.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
        for (const option of await popup.getByRole('option').all()) await expectReadableText(option);
        await page.screenshot({ path: info.outputPath('chat-initial-channel-open.png') });

        await trigger.press('End');
        await trigger.press('Enter');
        await expect(native).toHaveValue('private');
        await expect(page.getByTestId('chat-panel')).toHaveAttribute('data-channel', 'private');
        await expect(trigger).toHaveText('Private');
        await expect(trigger).toBeFocused();
        const recipient = page.getByTestId('chat-recipient-select').locator('..').getByRole('button');
        await expect(recipient).toHaveText('(pick a player)');
        await expectReadableText(recipient);
        await trigger.press('ArrowDown');
        await trigger.press('Home');
        await trigger.press('Enter');
        await expect(native).toHaveValue('table');
        await expect(page.getByTestId('chat-panel')).toHaveAttribute('data-channel', 'table');
        await expect(trigger).toHaveText('Table');
        await trigger.press('ArrowDown');
        await trigger.press('Escape');
        await expect(popup).toBeHidden();
        await expect(trigger).toBeFocused();
        await trigger.press('ArrowDown');
        await trigger.press('Tab');
        await expect(popup).toBeHidden();

        const input = page.getByTestId('chat-input');
        await expect(input).toBeEnabled();
        const message = `Chat ${viewport.width}: visible first line\nReadable second line\nStill here after reopening`;
        await input.fill(message);
        await page.getByTestId('chat-send').click();
        const body = page.getByTestId('chat-messages').locator('.chat-message-body').last();
        await expect(body).toHaveText(message);
        await expect(input).toBeEnabled();
        await input.fill('Keep this unsent draft');
        const collapse = compact ? toggle : page.getByTestId('chat-collapse');
        await collapse.click();
        await expect(toggle).toHaveAccessibleName('Open chat');
        await expect(toggle).toHaveAttribute('aria-expanded', 'false');
        await expect(page.locator('#chat-panel-body')).toBeHidden();
        await expect(toggle).toBeFocused();
        await toggle.press('Enter');
        await expect(toggle).toHaveAttribute('aria-expanded', 'true');
        await expect(input).toHaveValue('Keep this unsent draft');
        await expect(native).toHaveValue('table');
        await expect(body).toHaveText(message);
        await expectReadableText(body);
        await expectReadableText(trigger);
        await page.screenshot({ path: info.outputPath('chat-reopened-readable-message.png') });
        expect(actor.errors).toEqual([]);
      } finally {
        await context.close();
      }
    });
}

for (const { orientation, viewport } of [
  { orientation: 'portrait', viewport: { width: 390, height: 844 } },
  { orientation: 'short landscape', viewport: { width: 844, height: 390 } },
]) {
  test(`the ${orientation} spectator channel menu stays inside the viewport and selects the intended spectator channel`,
  async ({ browser, baseURL }, info) => {
    test.setTimeout(60_000);
    const context = await browser.newContext({
      viewport, hasTouch: true, isMobile: true, deviceScaleFactor: 1, locale: 'en-US',
    });
    try {
      const actor = await newActor(browser, baseURL, info, 'spectator-chat-readability', context);
      await applyRoom(actor, 0, -1);
      await closeLobby(actor);
      const panel = actor.page.getByTestId('chat-panel');
      const toggle = actor.page.getByTestId('chat-toggle');
      const spectator = actor.page.getByRole('region', { name: 'Spectator controls' });
      await expect(spectator).toBeInViewport({ ratio: 1 });
      await expectSeparatePanels(panel, spectator);
      await expectReadableText(toggle);
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expectSeparatePanels(panel, spectator);
      const native = actor.page.getByTestId('chat-channel-select');
      const wrapper = native.locator('..');
      const trigger = wrapper.getByRole('button', { name: 'Chat channel', exact: true });
      const popup = wrapper.getByRole('listbox', { name: 'Chat channel', exact: true });
      await expect(native).toHaveValue('spectators');
      await expect(trigger).toHaveText('Spectators');
      await expectReadableText(trigger);
      await trigger.click();
      await expect(popup).toBeInViewport({ ratio: 1 });
      await expect(popup.getByRole('option')).toHaveText(['Table', 'Spectators', 'Spectator DM', 'Private']);
      for (const option of await popup.getByRole('option').all()) await expectReadableText(option);
      await popup.getByRole('option', { name: 'Spectator DM', exact: true }).click();
      await expect(native).toHaveValue('spectator-private');
      await expect(actor.page.getByTestId('chat-panel')).toHaveAttribute('data-channel', 'spectator-private');
      await expect(trigger).toHaveText('Spectator DM');
      await expectReadableText(trigger);
      await expect(trigger).toBeFocused();

      await expect(spectator.getByRole('button')).toHaveCount(5);
      for (const button of await spectator.getByRole('button').all()) {
        await expectReadableText(button);
        await button.click();
        await expect(button).toHaveAttribute('aria-pressed', 'true');
      }
      const showAll = spectator.getByRole('checkbox', { name: 'Show all hands' });
      await showAll.check();
      await expect(showAll).toBeChecked();
      await showAll.uncheck();
      await expect(showAll).not.toBeChecked();
      await toggle.click();
      await expect(toggle).toHaveAccessibleName('Open chat');
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
      await expectSeparatePanels(panel, spectator);
      await expectReadableText(toggle);
      await expect(toggle).toBeFocused();
      await toggle.press('Enter');
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expectSeparatePanels(panel, spectator);
      await expect(native).toHaveValue('spectator-private');
      await expectReadableText(trigger);
      await actor.page.screenshot({ path: info.outputPath('spectator-chat-controls-open.png') });
      expect(actor.errors).toEqual([]);
    } finally {
      await context.close();
    }
  });
}
