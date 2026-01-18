import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

@Injectable({
    providedIn: 'root'
})
export class ChatSignalService {
    private resetSource = new Subject<void>();
    reset$ = this.resetSource.asObservable();

    // Persistent State
    messages: any[] = [];
    lastQuestion: string | undefined;
    lastSql: string | undefined;
    confirmedQuestion: string | undefined;
    confirmedSql: string | undefined;

    triggerReset() {
        this.resetSource.next();
        this.clearState();
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
    }
}
