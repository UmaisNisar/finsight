import { useRef, useState, type DragEvent } from 'react';

const carriesFiles = (event: DragEvent) => [...event.dataTransfer.types].includes('Files');

/**
 * Makes an element accept files dropped from the desktop. `dragging` is true while files hover over it, counting
 * enter and leave events so moving across its children doesn't flicker the highlight.
 */
export function useFileDrop(onFiles: (files: File[]) => void, disabled = false) {
  const [dragging, setDragging] = useState(false);
  const depth = useRef(0);

  const reset = () => {
    depth.current = 0;
    setDragging(false);
  };

  const handlers = {
    onDragEnter: (event: DragEvent) => {
      if (disabled || !carriesFiles(event)) return;
      event.preventDefault();
      depth.current += 1;
      setDragging(true);
    },
    onDragOver: (event: DragEvent) => {
      if (disabled || !carriesFiles(event)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
    },
    onDragLeave: (event: DragEvent) => {
      if (disabled || !carriesFiles(event)) return;
      depth.current = Math.max(0, depth.current - 1);
      if (depth.current === 0) setDragging(false);
    },
    onDrop: (event: DragEvent) => {
      if (disabled || !carriesFiles(event)) return;
      event.preventDefault();
      reset();
      const files = [...event.dataTransfer.files];
      if (files.length > 0) onFiles(files);
    },
  };

  return { dragging, handlers };
}
