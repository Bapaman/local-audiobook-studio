import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import type { Segment } from '../../api/types';
import { SegmentTable } from './SegmentTable';

function fragment(number: number, spokenText = 'Тестовая строка.', outputArtifactId: string | null = null): Segment {
  return { id: `s${number}`, order: number - 1, originalText: spokenText, normalizedText: spokenText,
    spokenText, contentHash: 'test', status: 'draft', outputArtifactId };
}

it('plays the previous recording before the current one and cancels a stopped comparison', async () => {
  const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
  const pause = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
  try {
    const user = userEvent.setup();
    const segment = {
      ...fragment(1, 'Новая строка.', 'new-audio'),
      previousAudio: { artifactId: 'old-audio', spokenText: 'Старая строка.' },
    };
    render(<SegmentTable projectId="book1" segments={[segment]} refreshVersions={{}} busy={false} onSave={vi.fn()} />);
    const previous = screen.getByLabelText<HTMLAudioElement>('Предыдущая запись фрагмента 1');
    const current = screen.getByLabelText<HTMLAudioElement>('Аудио фрагмента 1');
    const start = () => user.click(screen.getByRole('button', { name: 'Сравнить записи · фрагмент 1' }));
    await start();
    expect(play.mock.instances).toEqual([previous]);
    expect(screen.getByText('Играет предыдущая запись')).toBeVisible();
    Object.defineProperty(previous, 'ended', { configurable: true, value: true });
    fireEvent.ended(previous);
    expect(play.mock.instances).toEqual([previous, current]);
    expect(screen.getByText('Играет текущая запись')).toBeVisible();
    Object.defineProperty(current, 'ended', { configurable: true, value: true });
    fireEvent.ended(current);
    expect(screen.getByRole('button', { name: 'Сравнить записи · фрагмент 1' })).toBeEnabled();

    await start();
    await user.click(screen.getByRole('button', { name: 'Остановить сравнение · фрагмент 1' }));
    expect(pause.mock.instances).toContain(previous);
    fireEvent.ended(previous);
    expect(play.mock.instances).toEqual([previous, current, previous]);
  } finally {
    play.mockRestore();
    pause.mockRestore();
  }
});

it('adds or removes stress on the selected vowel and saves it as a spoken-text draft', async () => {
  const user = userEvent.setup();
  const onSave = vi.fn().mockResolvedValue(undefined);
  const onDirtyChange = vi.fn();
  render(<SegmentTable projectId="book1" busy={false} segments={[fragment(1, 'Старый замок.')]}
    refreshVersions={{}} onSave={onSave} onDirtyChange={onDirtyChange} />);
  const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Текст фрагмента 1' });
  const stress = screen.getByRole('button', { name: 'Ударение · фрагмент 1' });
  field.focus();
  field.setSelectionRange(8, 9);
  await user.click(stress);
  expect(field).toHaveValue('Старый за\u0301мок.');
  expect(field).toHaveFocus();
  expect([field.selectionStart, field.selectionEnd]).toEqual([10, 10]);
  expect(onDirtyChange).toHaveBeenLastCalledWith(true);
  expect(onSave).not.toHaveBeenCalled();

  await user.click(stress);
  expect(field).toHaveValue('Старый замок.');
  expect(onDirtyChange).toHaveBeenLastCalledWith(false);
  await user.click(stress);
  await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' }));
  expect(onSave).toHaveBeenCalledExactlyOnceWith('s1', 'Старый за\u0301мок.');
});

it('keeps the text intact when stress is requested for a range or a full fragment', async () => {
  const user = userEvent.setup();
  const full = 'а'.repeat(320);
  const props = { projectId: 'book1', busy: false, refreshVersions: {}, onSave: vi.fn() };
  render(<SegmentTable {...props} segments={[fragment(1, full)]} />);
  const result = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Текст фрагмента 1' });
  const stress = screen.getByRole('button', { name: 'Ударение · фрагмент 1' });
  result.focus();
  result.setSelectionRange(0, 2);
  await user.click(stress);
  expect(screen.getByText('Выделите одну гласную или поставьте курсор сразу после неё.')).toBeVisible();
  result.setSelectionRange(1, 1);
  await user.click(stress);
  expect(screen.getByText('Для знака ударения нужно освободить место в тексте фрагмента.')).toBeVisible();
  expect(result).toHaveValue(full);
  expect(props.onSave).not.toHaveBeenCalled();
});

it('retains an off-page draft through filtering and saves it from the draft list', async () => {
  const segments = Array.from({ length: 15 }, (_, index) => fragment(index + 1));
  segments[1] = { ...segments[1], outputArtifactId: 'verified-audio' };
  const onSave = vi.fn().mockResolvedValue(undefined);
  const onDirtyChange = vi.fn();
  const user = userEvent.setup();
  const props = { projectId: 'book1', busy: false, onSave, onDirtyChange };
  const { rerender } = render(<SegmentTable {...props} segments={segments} refreshVersions={{}} />);

  expect(screen.queryByLabelText('Текст фрагмента 13')).not.toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Следующая страница' }));
  fireEvent.change(screen.getByLabelText('Текст фрагмента 13'), { target: { value: 'Черновик на другой странице.' } });
  expect(onDirtyChange).toHaveBeenLastCalledWith(true);
  await user.selectOptions(screen.getByLabelText('Фильтр фрагментов'), 'ready');
  expect(screen.getByLabelText('Текст фрагмента 2')).toBeVisible();
  expect(screen.queryByLabelText('Текст фрагмента 13')).not.toBeInTheDocument();
  expect(onDirtyChange).toHaveBeenLastCalledWith(true);
  await user.click(screen.getByRole('button', { name: 'Показать правки · 1' }));
  expect(screen.getByLabelText('Текст фрагмента 13')).toHaveValue('Черновик на другой странице.');
  await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 13' }));
  expect(onSave).toHaveBeenCalledExactlyOnceWith('s13', 'Черновик на другой странице.');

  rerender(<SegmentTable {...props} refreshVersions={{ s13: 1 }} segments={segments.map(segment =>
    segment.id === 's13' ? { ...segment, spokenText: 'Черновик на другой странице.' } : segment)} />);
  expect(onDirtyChange).toHaveBeenLastCalledWith(false);
  expect(screen.queryByRole('button', { name: /Показать правки/ })).not.toBeInTheDocument();
});

it('finds Russian text without case, yo or accents and updates audio filters from accepted outputs', async () => {
  const user = userEvent.setup();
  const segments = [fragment(1), { ...fragment(2, 'Ёлка рядом.'), normalizedText: 'Ма\u0301ма рядом с ёлкой.' }, fragment(3)];
  const props = { projectId: 'book1', busy: false, onSave: vi.fn(), refreshVersions: {} };
  const { rerender } = render(<SegmentTable {...props} segments={segments} />);
  const search = screen.getByLabelText('Найти фрагмент');
  await user.type(search, 'МАМА рядом с елкой');
  expect(screen.getByLabelText('Текст фрагмента 2')).toBeVisible();
  expect(screen.queryByLabelText('Текст фрагмента 1')).not.toBeInTheDocument();

  await user.selectOptions(screen.getByLabelText('Фильтр фрагментов'), 'missing');
  rerender(<SegmentTable {...props} segments={segments.map(segment =>
    segment.id === 's2' ? { ...segment, outputArtifactId: 'verified-audio' } : segment)} />);
  expect(screen.getByText('Фрагменты не найдены.')).toBeVisible();
  await user.click(screen.getByRole('button', { name: 'Сбросить поиск и фильтр' }));
  await user.type(search, '#3');
  expect(screen.getByLabelText('Текст фрагмента 3')).toBeVisible();
  expect(screen.queryByLabelText('Текст фрагмента 2')).not.toBeInTheDocument();
});
