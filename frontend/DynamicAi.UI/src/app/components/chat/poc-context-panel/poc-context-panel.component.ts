import {
    ChangeDetectorRef,
    Component,
    EventEmitter,
    Input,
    OnInit,
    Output,
    inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { ShippedPageContext } from '../../../models';
import { PAGE_244_PRESET_CONTEXT } from '../../../presets/page244.preset';
import { ApiService } from '../../../services/api.service';
import { PocSettingsService } from '../../../services/poc-settings.service';

export interface PocConnectionState {
    parsedContext: ShippedPageContext | null;
    contextJsonError: string;
    comID: number;
    tenantId: string;
    tenantKey: string;
    connectionString: string;
}

@Component({
    selector: 'app-poc-context-panel',
    standalone: true,
    imports: [CommonModule, FormsModule],
    templateUrl: './poc-context-panel.component.html',
    styleUrls: ['./poc-context-panel.component.css']
})
export class PocContextPanelComponent implements OnInit {
    @Input() showSettingsPanel = false;
    @Input() validationMessage = '';
    @Output() contextChanged = new EventEmitter<PocConnectionState>();

    private apiService = inject(ApiService);
    private pocSettings = inject(PocSettingsService);
    private cdr = inject(ChangeDetectorRef);

    selectedPreset: 'page244' | 'custom' = 'page244';
    comID = 1;
    tenantId = 'default';
    tenantKey = '';
    connectionString = '';
    explicitContextJson = '';
    contextJsonError = '';
    parsedContext: ShippedPageContext | null = null;
    clearingCache = false;
    cacheActionMessage = '';
    cacheActionError = false;

    private readonly formattedPage244Preset = JSON.stringify(PAGE_244_PRESET_CONTEXT, null, 2);

    ngOnInit(): void {
        const snapshot = this.pocSettings.getSnapshot();
        this.comID = snapshot.comID ?? 1;
        this.tenantId = snapshot.tenantId || 'default';
        this.tenantKey = snapshot.tenantKey || '';
        this.connectionString = snapshot.connectionString || '';

        if (snapshot.explicitContextJson && snapshot.explicitContextJson.trim().length > 0) {
            this.explicitContextJson = snapshot.explicitContextJson;
            this.selectedPreset =
                snapshot.explicitContextJson.trim() === this.formattedPage244Preset.trim()
                    ? 'page244'
                    : 'custom';
        } else {
            this.explicitContextJson = this.formattedPage244Preset;
            this.selectedPreset = 'page244';
            this.pocSettings.save({ explicitContextJson: this.explicitContextJson });
        }

        this.validateContextJson(this.explicitContextJson);
        queueMicrotask(() => this.emitContextState());
    }

    getConnectionState(): PocConnectionState {
        return {
            parsedContext: this.parsedContext,
            contextJsonError: this.contextJsonError,
            comID: Number.isFinite(Number(this.comID)) ? Number(this.comID) : 1,
            tenantId: this.tenantId || 'default',
            tenantKey: this.tenantKey,
            connectionString: this.connectionString
        };
    }

    onPresetChange(preset: 'page244' | 'custom'): void {
        this.selectedPreset = preset;
        if (preset === 'page244') {
            this.explicitContextJson = this.formattedPage244Preset;
            this.validateContextJson(this.explicitContextJson);
            this.pocSettings.save({ explicitContextJson: this.explicitContextJson });
            this.emitContextState();
        }
    }

    resetPreset(): void {
        this.selectedPreset = 'page244';
        this.explicitContextJson = this.formattedPage244Preset;
        this.cacheActionMessage = '';
        this.cacheActionError = false;
        this.validateContextJson(this.explicitContextJson);
        this.pocSettings.save({ explicitContextJson: this.explicitContextJson });
        this.emitContextState();
    }

    onSettingsFieldChange(): void {
        const normalizedComId = Number.isFinite(Number(this.comID)) ? Number(this.comID) : 1;
        this.pocSettings.save({
            comID: normalizedComId,
            tenantId: this.tenantId || 'default',
            tenantKey: this.tenantKey,
            connectionString: this.connectionString
        });
        this.emitContextState();
    }

    onContextJsonChange(value: string): void {
        this.explicitContextJson = value;
        this.selectedPreset =
            value.trim() === this.formattedPage244Preset.trim() ? 'page244' : 'custom';
        this.validateContextJson(value);
        this.pocSettings.save({ explicitContextJson: value });
        this.emitContextState();
    }

    clearPageCache(): void {
        if (!this.parsedContext?.pageId) {
            this.cacheActionMessage = '⚠️ يرجى إدخال ExplicitContext JSON صالح يحتوي على pageId.';
            this.cacheActionError = true;
            return;
        }

        const targetPageId = this.parsedContext.pageId;
        this.clearingCache = true;
        this.cacheActionMessage = '';
        this.cacheActionError = false;

        this.apiService.clearCacheByPage(targetPageId).subscribe({
            next: (res) => {
                this.clearingCache = false;
                this.cacheActionError = false;
                this.cacheActionMessage =
                    res?.message || `✅ تم مسح الكاش للصفحة (${targetPageId}) بنجاح.`;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.clearingCache = false;
                this.cacheActionError = true;
                if (err?.status === 401) {
                    this.cacheActionMessage =
                        '🔑 مطلوب إدخال مفتاح X-Api-Key صالح في الشريط العلوي (وضع الحماية Strict مفعّل).';
                } else if (err?.status === 403) {
                    this.cacheActionMessage =
                        '⛔ هذا الإجراء يتطلب مفتاح X-Api-Key بدور Admin.';
                } else {
                    this.cacheActionMessage =
                        err?.error?.detail ||
                        err?.error?.message ||
                        `❌ فشل مسح الكاش للصفحة (${targetPageId}).`;
                }
                this.cdr.detectChanges();
            }
        });
    }

    private emitContextState(): void {
        this.contextChanged.emit(this.getConnectionState());
    }

    private validateContextJson(rawJson: string): void {
        if (!rawJson || !rawJson.trim()) {
            this.parsedContext = null;
            this.contextJsonError = 'ExplicitContext JSON is required.';
            return;
        }

        try {
            const parsed = JSON.parse(rawJson);
            if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
                this.parsedContext = null;
                this.contextJsonError = 'ExplicitContext must be a JSON object.';
                return;
            }
            if (typeof parsed.pageId !== 'number' || !Number.isFinite(parsed.pageId)) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "pageId" must be a valid number.';
                return;
            }
            if (typeof parsed.pageName !== 'string' || !parsed.pageName.trim()) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "pageName" must be a non-empty string.';
                return;
            }
            if (
                !parsed.masterSetup ||
                typeof parsed.masterSetup !== 'object' ||
                typeof parsed.masterSetup.tableName !== 'string' ||
                !parsed.masterSetup.tableName.trim()
            ) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "masterSetup.tableName" is required.';
                return;
            }
            if (
                typeof parsed.masterSetup.primaryKey !== 'string' ||
                !parsed.masterSetup.primaryKey.trim()
            ) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "masterSetup.primaryKey" is required.';
                return;
            }
            if (!Array.isArray(parsed.masterSetup.columns)) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "masterSetup.columns" must be an array.';
                return;
            }
            if (!Array.isArray(parsed.detailSetups)) {
                this.parsedContext = null;
                this.contextJsonError = 'Field "detailSetups" must be an array.';
                return;
            }

            this.parsedContext = parsed as ShippedPageContext;
            this.contextJsonError = '';
        } catch (err: any) {
            this.parsedContext = null;
            this.contextJsonError = `Invalid JSON: ${err?.message || 'Syntax error'}`;
        }
    }
}
