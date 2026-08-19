import React from "react";
import { createRoot } from "react-dom/client";
import { ManagementHostApp } from "./ManagementHostApp.jsx";
import "./management.css";
import { installManagementBridge } from "./managementBridge.js";

installManagementBridge();

createRoot(document.getElementById("root")).render(
  <React.StrictMode><ManagementHostApp /></React.StrictMode>,
);
