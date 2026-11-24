import { Injectable } from '@angular/core';

export interface PocSettings {
    apiKey: string;
    tenantKey: string;
    connectionString: string;
    comID: number;
    tenantId: string;
    explicitContextJson: string;
}

const STORAGE_KEY = 'dynamicai_poc_settings';

const DEFAULT_SETTINGS: PocSettings = {
    apiKey: '',
    tenantKey: '',
    connectionString: '',
    comID: 1,
    tenantId: 'default',
    explicitContextJson: ''
};

/**
 * Persists POC configuration (API key, tenant/connection settings, explicit page context JSON)
 * in localStorage for use across the app and the X-Api-Key HTTP interceptor.
 */
@Injectable({
    providedIn: 'root'
})
export class PocSettingsService {
    private settings: PocSettings = this.load();

    get apiKey(): string {
        return this.settings.apiKey;
    }

    set apiKey(value: string) {
        this.settings.apiKey = value ?? '';
        this.persist();
    }

    get tenantKey(): string {
        return this.settings.tenantKey;
    }

    set tenantKey(value: string) {
        this.settings.tenantKey = value ?? '';
        this.persist();
    }

    get connectionString(): string {
        return this.settings.connectionString;
    }

    set connectionString(value: string) {
        this.settings.connectionString = value ?? '';
        this.persist();
    }

    get comID(): number {
        return this.settings.comID;
    }

    set comID(value: number) {
        const parsed = Number(value);
        this.settings.comID = Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_SETTINGS.comID;
        this.persist();
    }

    get tenantId(): string {
        return this.settings.tenantId;
    }

    set tenantId(value: string) {
        this.settings.tenantId = value?.trim() || DEFAULT_SETTINGS.tenantId;
        this.persist();
    }

    get explicitContextJson(): string {
        return this.settings.explicitContextJson;
    }

    set explicitContextJson(value: string) {
        this.settings.explicitContextJson = value ?? '';
        this.persist();
    }

    save(partial: Partial<PocSettings>): void {
        const next: PocSettings = {
            ...this.settings,
            ...partial
        };
        next.apiKey = next.apiKey ?? '';
        next.tenantKey = next.tenantKey ?? '';
        next.connectionString = next.connectionString ?? '';
        const parsedComId = Number(next.comID);
        next.comID = Number.isFinite(parsedComId) && parsedComId > 0 ? parsedComId : DEFAULT_SETTINGS.comID;
        next.tenantId = next.tenantId?.trim() || DEFAULT_SETTINGS.tenantId;
        next.explicitContextJson = next.explicitContextJson ?? '';
        this.settings = next;
        this.persist();
    }

    getSnapshot(): PocSettings {
        return { ...this.settings };
    }

    clearApiKey(): void {
        this.settings.apiKey = '';
        this.persist();
    }

    private load(): PocSettings {
        try {
            if (typeof localStorage === 'undefined') {
                return { ...DEFAULT_SETTINGS };
            }
            const raw = localStorage.getItem(STORAGE_KEY);
            if (!raw) {
                return { ...DEFAULT_SETTINGS };
            }
            const parsed = JSON.parse(raw) as Partial<PocSettings>;
            const parsedComId = Number(parsed.comID);
            return {
                apiKey: typeof parsed.apiKey === 'string' ? parsed.apiKey : DEFAULT_SETTINGS.apiKey,
                tenantKey: typeof parsed.tenantKey === 'string' ? parsed.tenantKey : DEFAULT_SETTINGS.tenantKey,
                connectionString: typeof parsed.connectionString === 'string' ? parsed.connectionString : DEFAULT_SETTINGS.connectionString,
                comID: Number.isFinite(parsedComId) && parsedComId > 0 ? parsedComId : DEFAULT_SETTINGS.comID,
                tenantId: typeof parsed.tenantId === 'string' && parsed.tenantId.trim() ? parsed.tenantId : DEFAULT_SETTINGS.tenantId,
                explicitContextJson: typeof parsed.explicitContextJson === 'string' ? parsed.explicitContextJson : DEFAULT_SETTINGS.explicitContextJson
            };
        } catch {
            return { ...DEFAULT_SETTINGS };
        }
    }

    private persist(): void {
        try {
            if (typeof localStorage !== 'undefined') {
                localStorage.setItem(STORAGE_KEY, JSON.stringify(this.settings));
            }
        } catch {
            // Ignore storage quota/availability errors in restricted environments.
        }
    }
}
