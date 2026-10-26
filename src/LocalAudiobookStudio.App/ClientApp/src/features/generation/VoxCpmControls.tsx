import { useEffect, useState } from 'react';
import { StudioClient } from '../../api/client';
import type { VoxCpmGenerationOptions, VoxCpmReferenceCatalog } from '../../api/types';
import { validVoxOptions, voxDefaults } from './voxCpmOptions';

export function useVoxCpmReferences(client: StudioClient, projectId: string, modelId: string | undefined) {
  const scope = `${projectId}:${modelId ?? ''}`;
  const [result, setResult] = useState<{ scope: string; value?: VoxCpmReferenceCatalog; error?: string }>();
  useEffect(() => {
    if (!modelId) return;
    const abort = new AbortController(); let active = true;
    setResult({ scope });
    void client.voxCpmReferences(projectId, modelId, abort.signal).then(value => {
      if (active) setResult(value.modelId === modelId ? { scope, value } : { scope, error: 'Каталог опорных клипов не соответствует выбранной модели.' });
    }).catch(() => { if (active) setResult({ scope, error: 'Не удалось загрузить подтверждённые опорные клипы. Выберите другой голос или отключите опорный клип.' }); });
    return () => { active = false; abort.abort(); };
  }, [client, projectId, modelId, scope]);
  return result?.scope === scope ? result : undefined;
}

export function VoxCpmControls({ projectId, modelId, options, onChange, disabled, references }: {
  projectId: string; modelId: string; options: VoxCpmGenerationOptions; onChange: (value: VoxCpmGenerationOptions) => void; disabled: boolean;
  references: ReturnType<typeof useVoxCpmReferences>;
}) {
  const catalog = references?.value;
  const selected = catalog?.clips.find(clip => clip.id === (options.referenceClipId ?? catalog.defaultClipId));
  const numeric = (key: 'seed' | 'timesteps' | 'cfg' | 'tempoPercent', value: number) => onChange({ ...options, [key]: value });
  const finite = (value: number) => Number.isFinite(value) ? value : '';
  return <fieldset className="generation-options" disabled={disabled}><legend>Настройки VoxCPM2</legend>
    <label htmlFor="vox-seed">Seed — вариация</label><input id="vox-seed" type="number" min={0} max={2147483647} step={1} value={finite(options.seed)} onChange={event => numeric('seed', event.target.valueAsNumber)} />
    <button type="button" className="quiet" onClick={() => { const value = new Uint32Array(1); crypto.getRandomValues(value); numeric('seed', value[0] & 0x7fffffff); }}>Новая вариация</button>
    <label htmlFor="vox-timesteps">Шаги синтеза</label><input id="vox-timesteps" type="number" min={4} max={20} step={1} value={finite(options.timesteps)} onChange={event => numeric('timesteps', event.target.valueAsNumber)} />
    <label htmlFor="vox-cfg">CFG</label><input id="vox-cfg" type="number" min={1} max={3} step="any" value={finite(options.cfg)} onChange={event => numeric('cfg', event.target.valueAsNumber)} />
    <p className="subtle">Шаги относятся к вычислениям синтеза, а не к обучению модели. Seed задаёт вариацию; CFG меняет силу следования условию. Больше шагов, другой Seed или CFG не гарантируют сходство и качество.</p>
    <label htmlFor="vox-tempo">Темп экспортируемого аудио, %</label><input id="vox-tempo" type="number" min={80} max={120} step={1} value={finite(options.tempoPercent)} onChange={event => numeric('tempoPercent', event.target.valueAsNumber)} />
    <p className="subtle">100% — исходный темп. Меньше 100% — медленнее, больше — быстрее. Дополнительная обработка приблизительно сохраняет высоту голоса и меняет экспортируемый WAV; возможны звуковые артефакты. При 100% эта обработка не применяется.</p>
    <label className="check-label"><input type="checkbox" checked={options.useReference} onChange={event => onChange({ ...options, useReference: event.target.checked, referenceClipId: null })} />Использовать опорный клип</label>
    <p className="subtle">При отключении опорного клипа выбранная обученная модель VoxCPM2 остаётся той же.</p>
    {options.useReference && <>
      {!references?.value && !references?.error && <p role="status">Загружаем подтверждённые клипы выбранной модели…</p>}
      {references?.error && <p className="notice warning">{references.error}</p>}
      {catalog && <><label htmlFor="vox-reference">Опорный клип</label><select id="vox-reference" value={options.referenceClipId ?? ''} onChange={event => onChange({ ...options, referenceClipId: event.target.value || null })}>
        <option value="">Автоматически · ближайший к 12 секундам</option>
        {catalog.clips.map(clip => <option key={clip.id} value={clip.id}>Клип {clip.trainingOrdinal + 1} · {clip.seconds.toFixed(1)} с · {clip.text}</option>)}
        {options.referenceClipId && !catalog.clips.some(clip => clip.id === options.referenceClipId) && <option value={options.referenceClipId} disabled>Сохранённый опорный клип недоступен</option>}
      </select>{!selected && <p className="notice warning">Подходящий подтверждённый клип недоступен. Выберите другой клип или отключите опорный клип.</p>}
      {selected && <><p>Опорный клип {selected.trainingOrdinal + 1} · {selected.seconds.toFixed(1)} с: {selected.text}</p><audio aria-label="Аудио опорного клипа" key={`${modelId}:${selected.id}`} controls preload="none" src={`/api/projects/${encodeURIComponent(projectId)}/voxcpm-generation/models/${encodeURIComponent(modelId)}/references/${encodeURIComponent(selected.id)}/audio`} /></>}</>}
    </>}
    {!validVoxOptions(options) && <p className="notice warning" role="alert">Проверьте настройки: Seed 0–2147483647, шаги 4–20, CFG 1–3, целый темп 80–120%.</p>}
    <button type="button" className="quiet" onClick={() => onChange(voxDefaults())}>Сбросить настройки</button>
  </fieldset>;
}
