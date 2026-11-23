import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable } from 'rxjs';
import { tap } from 'rxjs/operators';

export interface SystemFeatures {
    intentClassificationEnabled: boolean;
}

@Injectable({
    providedIn: 'root'
})
export class ConfigService {
    private readonly http = inject(HttpClient);
    private readonly featuresSubject = new BehaviorSubject<SystemFeatures>({ intentClassificationEnabled: true });
    
    public features$ = this.featuresSubject.asObservable();

    loadFeatures(): Observable<SystemFeatures> {
        return this.http.get<SystemFeatures>('/api/config/features').pipe(
            tap(features => this.featuresSubject.next(features))
        );
    }

    get features(): SystemFeatures {
        return this.featuresSubject.value;
    }
}
