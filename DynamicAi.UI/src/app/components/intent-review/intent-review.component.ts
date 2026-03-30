import { Component, OnInit, OnDestroy, inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription, interval } from 'rxjs';
import { switchMap } from 'rxjs/operators';
import { IntentReviewService } from '../../services/intent-review.service';
import { IntentReviewItemDto, ClearIntentRequest } from '../../models';

@Component({
    selector: 'app-intent-review',
    standalone: true,
    imports: [CommonModule, FormsModule],
    templateUrl: './intent-review.component.html',
    styleUrls: ['./intent-review.component.css']
})
export class IntentReviewComponent implements OnInit, OnDestroy {
    private readonly intentReview = inject(IntentReviewService);
    private readonly cdr = inject(ChangeDetectorRef);
    private pollSub?: Subscription;

    pending: IntentReviewItemDto[] = [];
    pendingCount = 0;
    loading = true;
    clearQuestion = '';
    clearLoading = false;
    clearMessage: string | null = null;

    error: string | null = null;

    // Expandable question rows
    expandedRows = new Set<string>();

    // Available intents for dropdown
    availableIntents: string[] = [];

    ngOnInit() {
        this.loadData();
        this.loadAvailableIntents();
        this.startPolling();
    }

    ngOnDestroy() {
        this.stopPolling();
    }

    // --- Auto-polling ---
    // --- Auto-polling ---
    startPolling() {
        // Disabled per user request
        // this.pollSub = interval(15000).pipe(
        //     switchMap(() => this.intentReview.getPending())
        // ).subscribe({
        //     next: (list) => {
        //         this.pending = list;
        //         this.pendingCount = list.length;
        //         this.cdr.detectChanges();
        //     }
        // });
    }

    stopPolling() {
        this.pollSub?.unsubscribe();
    }

    // --- Load available intents ---
    loadAvailableIntents() {
        this.intentReview.getAvailableIntents().subscribe({
            next: (intents) => {
                this.availableIntents = intents;
                this.cdr.detectChanges();
            },
            error: () => {
                // Fallback: hardcoded list
                this.availableIntents = ['DataRetrieval', 'Analysis', 'Prediction', 'PredictionWithDataFetch', 'Chat'];
            }
        });
    }

    // --- Expandable rows ---
    toggleExpand(id: string) {
        if (this.expandedRows.has(id)) {
            this.expandedRows.delete(id);
        } else {
            this.expandedRows.add(id);
        }
    }

    isExpanded(id: string): boolean {
        return this.expandedRows.has(id);
    }

    // --- Intent update ---
    onIntentChange(item: IntentReviewItemDto, newIntent: string) {
        if (newIntent === item.classifiedIntent) return;
        this.intentReview.updateIntent(item.id, newIntent).subscribe({
            next: (res) => {
                item.classifiedIntent = res.intentType;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.error?.message || 'Failed to update intent';
                this.cdr.detectChanges();
            }
        });
    }

    loadData() {
        this.loading = true;
        this.error = null;
        this.intentReview.getPending().subscribe({
            next: (list) => {
                this.pending = list;
                this.pendingCount = list.length;
                this.loading = false;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Failed to load pending examples';
                this.loading = false;
                this.cdr.detectChanges();
            }
        });
    }

    approve(item: IntentReviewItemDto) {
        this.intentReview.approve(item.id).subscribe({
            next: () => {
                this.pending = this.pending.filter((p) => p.id !== item.id);
                this.pendingCount = this.pending.length;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Approve failed';
                this.cdr.detectChanges();
            }
        });
    }

    reject(item: IntentReviewItemDto) {
        this.intentReview.reject(item.id).subscribe({
            next: () => {
                this.pending = this.pending.filter((p) => p.id !== item.id);
                this.pendingCount = this.pending.length;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Reject failed';
                this.cdr.detectChanges();
            }
        });
    }

    clearByQuestion() {
        const q = (this.clearQuestion || '').trim();
        if (!q) return;
        this.clearLoading = true;
        this.clearMessage = null;
        const req: ClearIntentRequest = {
            question: q,
            tenantId: 'default', // TODO: Get from AuthService
            companyId: 1        // TODO: Get from AuthService
        };
        this.intentReview.clearByQuestion(req).subscribe({
            next: (res) => {
                this.clearMessage = res.message || 'Done';
                this.clearQuestion = '';
                this.clearLoading = false;
                this.loadData();
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.clearMessage = err?.error?.error || err?.message || 'Clear failed';
                this.clearLoading = false;
                this.cdr.detectChanges();
            }
        });
    }
}
