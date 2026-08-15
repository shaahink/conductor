# Tasks: Greeting service

**Input**: Design documents from `/specs/001-greeting-service/`
**Prerequisites**: plan.md (required), research.md, contracts/

## Phase 3.1: Setup

- [ ] T001 Create the project skeleton per implementation plan
- [ ] T002 [P] Configure linting and formatting

## Phase 3.2: Tests First (TDD)

**CRITICAL: these tests MUST be written and MUST FAIL before ANY implementation**

- [ ] T003 [P] Contract test GET /greeting in tests/contract/test_greeting_get.py

## Phase 3.3: Core Implementation

- [ ] T004 Greeting model in src/models/greeting.py
- [ ] T005 GreetingService in src/services/greeting_service.py

## Dependencies

- T003 blocks T004
- T001 comes before everything else

## Notes

- [P] marks tasks that touch different files and may run in parallel.
