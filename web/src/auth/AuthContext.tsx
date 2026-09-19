import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { apiClient } from '../api/client';
import type { AuthResponse, LoginRequest, RegisterRequest } from '../api/types';

interface AuthState {
  token: string | null;
  userId: string | null;
  email: string | null;
  displayName: string | null;
  roles: string[];
}

interface AuthContextValue extends AuthState {
  login: (request: LoginRequest, remember?: boolean) => Promise<void>;
  register: (request: RegisterRequest) => Promise<void>;
  logout: () => void;
  hasRole: (role: string) => boolean;
}

const STORAGE_KEY = 'aireap.auth';
const EMPTY_STATE: AuthState = { token: null, userId: null, email: null, displayName: null, roles: [] };

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

function loadState(): AuthState {
  // sessionStorage (unchecked "remember me") wins if present; otherwise fall back to a
  // persisted localStorage session.
  const raw = sessionStorage.getItem(STORAGE_KEY) ?? localStorage.getItem(STORAGE_KEY);
  if (!raw) {
    return EMPTY_STATE;
  }
  try {
    return JSON.parse(raw) as AuthState;
  } catch {
    return EMPTY_STATE;
  }
}

function saveState(response: AuthResponse, remember: boolean): AuthState {
  const state: AuthState = {
    token: response.token,
    userId: response.userId,
    email: response.email,
    displayName: response.displayName,
    roles: response.roles,
  };
  const json = JSON.stringify(state);
  if (remember) {
    localStorage.setItem(STORAGE_KEY, json);
    sessionStorage.removeItem(STORAGE_KEY);
  } else {
    sessionStorage.setItem(STORAGE_KEY, json);
    localStorage.removeItem(STORAGE_KEY);
  }
  return state;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>(loadState);

  const value = useMemo<AuthContextValue>(
    () => ({
      ...state,
      login: async (request, remember = true) => {
        const response = await apiClient.post<AuthResponse>('/api/auth/login', request);
        setState(saveState(response, remember));
      },
      register: async (request) => {
        const response = await apiClient.post<AuthResponse>('/api/auth/register', request);
        setState(saveState(response, true));
      },
      logout: () => {
        localStorage.removeItem(STORAGE_KEY);
        sessionStorage.removeItem(STORAGE_KEY);
        setState(EMPTY_STATE);
      },
      hasRole: (role) => state.roles.includes(role),
    }),
    [state],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return ctx;
}
