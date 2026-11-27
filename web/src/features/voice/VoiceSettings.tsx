import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { getMeQueryKey, updateMeMutation } from '../../api/gen/@tanstack/react-query.gen';
import type { MeResponse, VoiceButton } from '../../api/gen/types.gen';

/** Profile settings for voice control (task 036): which button, and whether sure changes apply without asking. */
export function VoiceSettings({ me }: { me: MeResponse }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const update = useMutation({ ...updateMeMutation(), onSuccess: (profile) => qc.setQueryData(getMeQueryKey(), profile) });
  const buttons: VoiceButton[] = ['topBar', 'thumb'];
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-1 text-sm font-semibold">{t('voice.settings.title')}</legend>
      <div role="radiogroup" aria-label={t('voice.settings.button')} className="flex flex-col gap-1">
        {buttons.map((b) => (
          <label key={b} className="flex min-h-[var(--control)] items-center gap-2">
            <input
              type="radio"
              name="voice-button"
              checked={me.voiceButton === b}
              onChange={() => update.mutate({ body: { voiceButton: b } })}
            />
            <span>{t(`voice.settings.${b}`)}</span>
          </label>
        ))}
      </div>
      <label className="flex items-start gap-2">
        <input
          type="checkbox"
          className="mt-1"
          checked={me.voiceAutoApply}
          onChange={(e) => update.mutate({ body: { voiceAutoApply: e.target.checked } })}
        />
        <span className="flex flex-col">
          <span>{t('voice.settings.autoApply')}</span>
          <span className="text-sm text-mute">{t('voice.settings.autoApplyHint')}</span>
        </span>
      </label>
    </fieldset>
  );
}
