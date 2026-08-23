import { Routes } from '@angular/router';
import { SagaList } from './pages/saga-list/saga-list';
import { SagaDetail } from './pages/saga-detail/saga-detail';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'sagas' },
  { path: 'sagas', component: SagaList },
  { path: 'sagas/:id', component: SagaDetail },
];
