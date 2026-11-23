import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

@Injectable({
    providedIn: 'root'
})
export class ChatSignalService {
    private resetSource = new Subject<void>();
    reset$ = this.resetSource.asObservable();

    // Sidebar Toggle Signal (for communication with App shell)
    private sidebarToggleSource = new Subject<void>();
    sidebarToggle$ = this.sidebarToggleSource.asObservable();

    triggerSidebarToggle() {
        this.sidebarToggleSource.next();
    }

    // Persistent State
    messages: any[] = [];
    lastQuestion: string | undefined;
    lastSql: string | undefined;
    confirmedQuestion: string | undefined;
    confirmedSql: string | undefined;
    /** Session ID for conversation context (backend Layer -1). New per chat session. */
    private sessionId: string | undefined;

    triggerReset() {
        this.resetSource.next();
        this.clearState();
    }

    /** Returns current session ID, or creates one if missing. Clear on reset. */
    getOrCreateSessionId(): string {
        if (!this.sessionId) {
            this.sessionId = typeof crypto !== 'undefined' && crypto.randomUUID
                ? crypto.randomUUID()
                : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
                    const r = (Math.random() * 16) | 0;
                    const v = c === 'x' ? r : (r & 0x3) | 0x8;
                    return v.toString(16);
                });
        }
        return this.sessionId;
    }

    saveState(messages: any[], lastQ: string | undefined, lastSql: string | undefined, confQ: string | undefined, confSql: string | undefined) {
        this.messages = messages;
        this.lastQuestion = lastQ;
        this.lastSql = lastSql;
        this.confirmedQuestion = confQ;
        this.confirmedSql = confSql;
    }

    getState() {
        return {
            messages: this.messages,
            lastQuestion: this.lastQuestion,
            lastSql: this.lastSql,
            confirmedQuestion: this.confirmedQuestion,
            confirmedSql: this.confirmedSql
        };
    }

    clearState() {
        this.messages = [];
        this.lastQuestion = undefined;
        this.lastSql = undefined;
        this.confirmedQuestion = undefined;
        this.confirmedSql = undefined;
        this.sessionId = undefined;
    }
}
