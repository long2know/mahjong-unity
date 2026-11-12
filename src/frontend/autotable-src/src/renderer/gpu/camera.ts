export function orthographic4(size: number, aspect: number, near: number, far: number): Float32Array {
  const matrix = new Float32Array(16);
  matrix[0] = 1 / (size * aspect);
  matrix[5] = 1 / size;
  matrix[10] = -2 / (far - near);
  matrix[14] = -(far + near) / (far - near);
  matrix[15] = 1;
  return matrix;
}
