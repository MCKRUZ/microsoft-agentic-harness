import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { installBrowserLogger } from './lib/browserLogger'
import './index.css'
import App from './app/App'

installBrowserLogger()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
