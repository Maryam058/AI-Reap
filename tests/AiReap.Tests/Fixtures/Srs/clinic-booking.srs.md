SYNTHETIC TEST FIXTURE - invented content for automated tests only. No real organisation, person or system.

# Harbourview Community Clinic - Room Booking System

## 1. Purpose

The Harbourview Community Clinic opened in 2019 and serves about 40,000 residents across three neighbourhoods.
This document describes a system that lets clinic staff book consultation rooms.
The current paper diary is often double-booked, which wastes clinician time.

## 2. Glossary

Receptionist: a front-desk staff member who manages bookings.
Clinician: a doctor, nurse or therapist who sees patients.
Administrator: a staff member who manages user accounts and settings.

## 3. Booking

FR-001: Receptionists must be able to book a consultation room for a named clinician and a time slot.
FR-002: The system must prevent two bookings of the same room for overlapping time slots.
FR-003: Clinicians must be able to view their own bookings for the coming week.
FR-004: The system must send a confirmation email to the clinician when a booking is created.
FR-005: Priority patients must be offered the earliest available room.
FR-006: Receptionists may delete bookings that were created by mistake.

## 4. Cancellation

FR-007: Receptionists must be able to cancel a booking up to 24 hours before its start time.
FR-008: The system must record who cancelled a booking and when.

## 5. Roles and permissions

FR-009: Only administrators may delete bookings.
FR-010: Administrators must be able to create, disable and re-enable staff accounts.
FR-011: When a booking is made, the clinician receives an email confirming it.
Access to bookings follows the rules in FR-003 and FR-009.

## 6. Payments

FR-012: Patients pay a deposit when a room is booked for a private consultation.

## 7. Policies summary

Bookings can be cancelled up to 48 hours before the start time.
Patient records are kept separately in the clinic's existing records system.

## 8. Non-functional requirements

NFR-001: The booking page must load within 2 seconds for 95% of requests.
NFR-002: The system must support at least 50 concurrent users.
NFR-003: The system must be available 99.5% of the time each calendar month.
NFR-004: The system should be secure.
NFR-005: Staff passwords must be at least 12 characters long and are stored hashed with SHA-256.

## 9. Data retention

Booking records are retained as required by law.

## 10. Summary of requirements

The system must prevent two bookings of the same room for overlapping time slots.
