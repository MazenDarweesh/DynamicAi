import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

@Injectable({
    providedIn: 'root'
})
export class ChatSignalService {
    private resetSource = new Subject<void>();
    reset$ = this.resetSource.asObservable();

    triggerReset() {
        this.resetSource.next();
    }
}
