# HAFood-API

A scalable food ordering and e-commerce RESTful API built with ASP.NET WEB API.

## Features

### Authentication & Authorization
- JWT Authentication
- Refresh Token
- Role-Based Authorization
- Email Verification
- Password Reset

### User
- Profile Management
- Address Management
- Loyalty Points & Missions
- Notifications

### Product
- Category Management
- Product Management
- Product Search & Filtering
- Product Reviews & Ratings

### Cart & Orders
- Shopping Cart
- Checkout
- Order Tracking
- Shipping Management
- Order History

### Promotion
- Coupon System
- Promotions & Discounts

### Admin
- Dashboard & Statistics
- User Management
- Product Management
- Order Management
- Promotion Management

---

## Tech Stack

### Backend
- ASP.NET Core Web API
- Entity Framework Core
- SQL Server
- ASP.NET Identity
- JWT Authentication
- AutoMapper
- FluentValidation
- Swagger / OpenAPI

### Development Tools
- Git & GitHub
- Postman
- Docker
- GitHub Actions

---

## Architecture

```text
API Layer
    ↓
Application Layer
    ↓
Domain Layer
    ↓
Infrastructure Layer
    ↓
SQL Server
```

---

## Project Structure

```text
HAFood-API
├── Controllers
├── Application
├── Domain
├── Infrastructure
├── Persistence
├── DTOs
├── Services
├── Middleware
├── Extensions
├── Migrations
└── Configurations
```

---

## API Documentation

### Authentication
- POST /api/auth/register
- POST /api/auth/login
- POST /api/auth/refresh-token

### Product
- GET /api/products
- GET /api/products/{id}
- POST /api/products
- PUT /api/products/{id}
- DELETE /api/products/{id}

### Orders
- GET /api/orders
- POST /api/orders

---

## Database Design

### Main Entities

- Users
- Roles
- Products
- Categories
- Orders
- OrderItems
- Promotions
- Reviews
- Notifications

(Place ERD image here)

---

## Getting Started

### Clone Repository

```bash
git clone https://github.com/hoangan1610/HAFood-API.git
```

### Configure appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "YOUR_CONNECTION_STRING"
  },
  "Jwt": {
    "Key": "YOUR_SECRET_KEY"
  }
}
```

### Apply Migrations

```bash
dotnet ef database update
```

### Run Application

```bash
dotnet run
```

---

## Future Improvements

- Redis Caching
- CQRS + MediatR
- Background Jobs with Hangfire
- SignalR Notifications
- Unit Testing
- Integration Testing
- Docker Deployment
- CI/CD Pipeline
- API Rate Limiting

---

## Author

Ngo Hoang An

- GitHub: https://github.com/hoangan1610
- Email: hoanganngo469@gmail.com
- LinkedIn: linkedin.com/in/ngohoangan
