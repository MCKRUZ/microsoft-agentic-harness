import axios from 'axios';

// Token interceptor added in section 09
const apiClient = axios.create({
  baseURL: import.meta.env['VITE_API_BASE_URL'] as string | undefined,
});

export default apiClient;
