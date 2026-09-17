import { Component, type ErrorInfo, type ReactNode } from 'react';

export interface FallbackProps {
  error: unknown;
  /** Clears the error and renders the children again. */
  reset: () => void;
}

interface ErrorBoundaryProps {
  children: ReactNode;
  fallback: ReactNode | ((props: FallbackProps) => ReactNode);
  /** Called after `reset`, for example to refetch the data the failed content was showing. */
  onReset?: () => void;
  /** When any of these change, a shown error clears by itself (for example the pathname, or the selected period). */
  resetKeys?: readonly unknown[];
  onError?: (error: unknown, info: ErrorInfo) => void;
}

interface ErrorBoundaryState {
  error: unknown;
  hasError: boolean;
  resetKeys: readonly unknown[] | undefined;
}

const changed = (a: readonly unknown[] = [], b: readonly unknown[] = []) => a.length !== b.length || a.some((item, index) => !Object.is(item, b[index]));

/**
 * Catches render errors below it and shows `fallback` in their place, so one broken widget can't take down the
 * page around it. A small hand-written equivalent of react-error-boundary.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  override state: ErrorBoundaryState = { error: null, hasError: false, resetKeys: this.props.resetKeys };

  static getDerivedStateFromError(error: unknown): Partial<ErrorBoundaryState> {
    return { error, hasError: true };
  }

  static getDerivedStateFromProps(props: ErrorBoundaryProps, state: ErrorBoundaryState): Partial<ErrorBoundaryState> | null {
    if (changed(props.resetKeys, state.resetKeys)) {
      return state.hasError ? { error: null, hasError: false, resetKeys: props.resetKeys } : { resetKeys: props.resetKeys };
    }
    return null;
  }

  override componentDidCatch(error: unknown, info: ErrorInfo) {
    if (import.meta.env.DEV) {
      console.error('[FinSight] A component failed to render', error, info.componentStack);
    }
    this.props.onError?.(error, info);
  }

  reset = () => {
    this.setState({ error: null, hasError: false });
    this.props.onReset?.();
  };

  override render() {
    if (!this.state.hasError) return this.props.children;
    const { fallback } = this.props;
    return typeof fallback === 'function' ? fallback({ error: this.state.error, reset: this.reset }) : fallback;
  }
}
